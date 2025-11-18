using System.Collections.Concurrent;
using System.Net;
using System.Text;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers
{
    public class MetricsScrapingWorkerTests
    {
        // --- Fakes pour les nouvelles dépendances ---

        private sealed class TestRequestedMetricsRegistry : IRequestedMetricsRegistry
        {
            public void RegisterFromQuery(string promql) { /* no-op pour les tests */ }

            // On considère que toutes les métriques sont "demandées" pour garder
            // le comportement des tests existants.
            public bool IsRequestedKey(string metricKey) => true;

            public IReadOnlyCollection<string> GetRequestedMetricNames() => Array.Empty<string>();
        }

        private sealed class TestMetricsScrapingGuard : IMetricsScrapingGuard
        {
            public bool IsEnabled { get; set; }

            public void EnablePromql() => IsEnabled = true;
        }

        // --- Helpers de construction ---

        private static PodInformation Pod(string name, string ip, string deploymentName, IDictionary<string, string> anns)
            => new PodInformation(
                    Name: name,
                    Started: true,
                    Ready: true,
                    Ip: ip,
                    DeploymentName: deploymentName,
                    Ports: new List<int> { 5000 },
                    ResourceVersion: "1")
            { Annotations = anns };

        private static IDictionary<string, string> PromAnns(
            string scrape = "true",
            string port = "5000",
            string path = "/metrics",
            string? scheme = null)
        {
            var d = new Dictionary<string, string>
            {
                ["prometheus.io/scrape"] = scrape,
                ["prometheus.io/port"] = port,
                ["prometheus.io/path"] = path
            };
            if (!string.IsNullOrWhiteSpace(scheme))
                d["prometheus.io/scheme"] = scheme!;
            return d;
        }

        private static DeploymentsInformations BuildDeploymentsForScrape()
        {
            var depPods = new List<PodInformation>
            {
                Pod("dep-a-p1", "10.1.0.1", "dep-a", PromAnns(port:"5001")),
                Pod("dep-a-p2", "10.1.0.2", "dep-a", PromAnns(port:"5002"))
            };

            var depA = new DeploymentInformation(
                Deployment: "dep-a",
                Namespace: "ns",
                Pods: depPods,
                Configuration: new SlimFaasConfiguration(),
                Replicas: 2,
                ReplicasAtStart: 1,
                ReplicasMin: 0,
                TimeoutSecondBeforeSetReplicasMin: 300,
                NumberParallelRequest: 1,
                ReplicasStartAsSoonAsOneFunctionRetrieveARequest: false,
                PodType: PodType.Deployment,
                DependsOn: new List<string>(),
                Schedule: new ScheduleConfig(),
                SubscribeEvents: new List<SubscribeEvent>(),
                Visibility: FunctionVisibility.Public,
                PathsStartWithVisibility: new List<PathVisibility>(),
                ResourceVersion: "1",
                EndpointReady: true,
                Trust: FunctionTrust.Trusted
            );

            // SlimFaas pods (StatefulSet -0, -1) pour la désignation
            var slimfaasPods = new List<PodInformation>
            {
                new PodInformation("slimfaas-0", true, true, "10.9.0.1", "slimfaas", new List<int>{2112}, "1"),
                new PodInformation("slimfaas-1", true, true, "10.9.0.2", "slimfaas", new List<int>{2112}, "1"),
            };

            var slimfaas = new SlimFaasDeploymentInformation(2, slimfaasPods);

            return new DeploymentsInformations(
                Functions: new List<DeploymentInformation> { depA },
                SlimFaas: slimfaas,
                Pods: new List<PodInformation>());
        }

        private sealed class MapHttpHandler : HttpMessageHandler
        {
            private readonly ConcurrentDictionary<string, (HttpStatusCode code, string body)> _map;

            public MapHttpHandler(IEnumerable<(string url, HttpStatusCode code, string body)> entries)
            {
                _map = new ConcurrentDictionary<string, (HttpStatusCode, string)>(
                    entries.ToDictionary(e => e.url, e => (e.code, e.body)));
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var url = request.RequestUri!.ToString();
                if (_map.TryGetValue(url, out var res))
                {
                    var msg = new HttpResponseMessage(res.code);
                    msg.Content = new StringContent(res.body, Encoding.UTF8, "text/plain");
                    return Task.FromResult(msg);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("", Encoding.UTF8, "text/plain")
                });
            }
        }

        private static InMemoryMetricsStore CreateStore()
        {
            var registry = new TestRequestedMetricsRegistry();
            return new InMemoryMetricsStore(registry, retentionSeconds: 3600);
        }

        private static MetricsScrapingWorker NewWorker(
            DeploymentsInformations deployments,
            HttpClient httpClient,
            string currentPodName,
            InMemoryMetricsStore store,
            bool scrapingEnabled = true,
            int delayMs = 10_000) // le scraping se fait avant le premier Delay
        {
            // HOSTNAME courant
            Environment.SetEnvironmentVariable("HOSTNAME", currentPodName);

            // IReplicasService
            var replicas = new Mock<IReplicasService>();
            replicas.SetupGet(r => r.Deployments).Returns(deployments);

            // IRaftCluster : leader null => plus petit ordinal désigné.
            var cluster = new Mock<IRaftCluster>();
            cluster.SetupGet(c => c.Leader).Returns((IClusterMember?)null);
            cluster.SetupGet(c => c.Members).Returns(Array.Empty<IRaftClusterMember>() as IReadOnlyCollection<IRaftClusterMember>);

            // IHttpClientFactory
            var httpFactory = new Mock<IHttpClientFactory>();
            httpFactory
                .Setup(f => f.CreateClient(It.IsAny<string>()))
                .Returns(httpClient);

            // ISlimDataStatus
            var status = new Mock<ISlimDataStatus>();
            status.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

            // IMetricsScrapingGuard : on force IsEnabled=true pour les tests (pas de ScaleConfig).
            var guard = new TestMetricsScrapingGuard { IsEnabled = scrapingEnabled };

            // ILogger
            var logger = Mock.Of<ILogger<MetricsScrapingWorker>>();

            return new MetricsScrapingWorker(
                replicasService: replicas.Object,
                cluster: cluster.Object,
                httpClientFactory: httpFactory.Object,
                metricsStore: store,
                slimDataStatus: status.Object,
                scrapingGuard: guard,
                logger: logger,
                delay: delayMs
            );
        }

        private static async Task StartRunOnceAndStopAsync(BackgroundService svc, int settleMs = 250)
        {
            await svc.StartAsync(CancellationToken.None);
            await Task.Delay(settleMs);
            await svc.StopAsync(CancellationToken.None);
        }

        // --- Tests ---

        [Fact]
        public async Task Designated_Node_Scrapes_And_Stores_Metrics()
        {
            var deployments = BuildDeploymentsForScrape();

            // Cibles construites par MetricsExtensions
            var urls = deployments.GetMetricsTargets();
            var depAUrls = urls["dep-a"].OrderBy(x => x).ToArray();
            Assert.Equal(2, depAUrls.Length);

            // Réponses Prometheus
            var body1 = """
                        # HELP metric_one test
                        metric_one 1
                        metric_two{label="a"} 2.5
                        metric_nan NaN
                        """;

            var body2 = """
                        metric_one 3
                        metric_two{label="a"} 4.5
                        metric_inf +Inf
                        metric_ninf -Inf
                        """;

            var handler = new MapHttpHandler(new[]
            {
                (depAUrls[0], HttpStatusCode.OK, body1),
                (depAUrls[1], HttpStatusCode.OK, body2)
            });
            var http = new HttpClient(handler);

            var store = CreateStore();
            var worker = NewWorker(deployments, http, currentPodName: "slimfaas-0", store: store);

            await StartRunOnceAndStopAsync(worker);

            var snapshot = store.Snapshot();
            Assert.NotEmpty(snapshot);

            // Un seul timestamp attendu pour cette passe
            var (ts, depMap) = snapshot.OrderBy(kv => kv.Key).Last();

            Assert.True(depMap.ContainsKey("dep-a"));
            var podMap = depMap["dep-a"];

            Assert.True(podMap.ContainsKey("10.1.0.1"));
            Assert.True(podMap.ContainsKey("10.1.0.2"));

            var m1 = podMap["10.1.0.1"];
            var m2 = podMap["10.1.0.2"];

            // NaN/Inf ignorés, metrics parsées
            Assert.Equal(2, m1.Count);
            Assert.Equal(2, m2.Count);

            Assert.Equal(1.0, m1["metric_one"], 6);
            Assert.Equal(2.5, m1[@"metric_two{label=""a""}"], 6);

            Assert.Equal(3.0, m2["metric_one"], 6);
            Assert.Equal(4.5, m2[@"metric_two{label=""a""}"], 6);
        }

        [Fact]
        public async Task Non_Designated_Node_Does_Not_Scrape()
        {
            var deployments = BuildDeploymentsForScrape();

            var handler = new MapHttpHandler(Array.Empty<(string, HttpStatusCode, string)>());
            var http = new HttpClient(handler);

            // Node courant = slimfaas-1 (le plus petit ordinal est -0) => pas désigné
            var store = CreateStore();
            var worker = NewWorker(deployments, http, currentPodName: "slimfaas-1", store: store);

            await StartRunOnceAndStopAsync(worker);

            var snapshot = store.Snapshot();
            Assert.True(snapshot.Count == 0); // rien scrapé
        }

        [Fact]
        public async Task Non_Success_Http_Is_Ignored()
        {
            var deployments = BuildDeploymentsForScrape();
            var urls = deployments.GetMetricsTargets()["dep-a"].OrderBy(x => x).ToArray();

            var handler = new MapHttpHandler(new[]
            {
                (urls[0], HttpStatusCode.OK, "metric_ok 123"),
                (urls[1], HttpStatusCode.InternalServerError, "metric_fail 1"),
            });
            var http = new HttpClient(handler);

            var store = CreateStore();
            var worker = NewWorker(deployments, http, currentPodName: "slimfaas-0", store: store);

            await StartRunOnceAndStopAsync(worker);

            var snapshot = store.Snapshot();
            Assert.NotEmpty(snapshot);
            var depMap = snapshot.Values.Single();

            var podMap = depMap["dep-a"];
            Assert.True(podMap.ContainsKey("10.1.0.1"));
            Assert.False(podMap.ContainsKey("10.1.0.2")); // 500 => ignoré

            var m1 = podMap["10.1.0.1"];
            Assert.Single(m1);
            Assert.Equal(123.0, m1["metric_ok"], 6);
        }

        [Fact]
        public async Task Parse_With_Labels_And_Optional_Timestamp_Works()
        {
            var anns = new Dictionary<string, string> {
                ["prometheus.io/scrape"] = "true",
                ["prometheus.io/port"] = "5000",
                ["prometheus.io/path"] = "/metrics"
            };

            var depPod = new PodInformation("dep-a-p1", true, true, "10.1.0.1", "dep-a", new List<int>{5000}, "1")
            {
                Annotations = anns
            };

            var depA = new DeploymentInformation(
                "dep-a", "ns", new List<PodInformation>{ depPod }, new SlimFaasConfiguration(),
                1, 1, 0, 300, 1, false, PodType.Deployment,
                new List<string>(), new ScheduleConfig(), new List<SubscribeEvent>(), FunctionVisibility.Public,
                new List<PathVisibility>(), "1", true, FunctionTrust.Trusted);

            var deployments = new DeploymentsInformations(
                new List<DeploymentInformation>{ depA },
                new SlimFaasDeploymentInformation(2, new List<PodInformation>{
                    new("slimfaas-0", true, true, "10.9.0.1", "slimfaas", new List<int>{2112}, "1"),
                    new("slimfaas-1", true, true, "10.9.0.2", "slimfaas", new List<int>{2112}, "1"),
                }),
                new List<PodInformation>());

            var urls = deployments.GetMetricsTargets();
            var url = Assert.Single(urls["dep-a"]);

            var body = """
                       # HELP something
                       metric_one 1 1731200000
                       metric_two{label="a"} 2.5
                       """;

            var handler = new MapHttpHandler(new[] { (url, HttpStatusCode.OK, body) });
            var http = new HttpClient(handler);

            var store = CreateStore();
            var worker = NewWorker(deployments, http, currentPodName: "slimfaas-0", store: store);

            await StartRunOnceAndStopAsync(worker);

            var snapshot = store.Snapshot();
            var depMap = snapshot.Values.Single();
            var podMap = depMap["dep-a"];
            var m = podMap["10.1.0.1"];

            Assert.Equal(2, m.Count);
            Assert.Equal(1.0, m["metric_one"], 6);
            Assert.Equal(2.5, m[@"metric_two{label=""a""}"], 6);
        }
    }
}
