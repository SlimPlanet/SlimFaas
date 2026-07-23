using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;
using Xunit;

namespace SlimFaas.Tests.Workers
{
    public class MetricsScrapingWorkerTests
    {
        // --- Fakes pour les nouvelles dépendances ---

        private sealed class TestRequestedMetricsRegistry : IRequestedMetricsRegistry
        {
            private readonly HashSet<string> _names;

            public TestRequestedMetricsRegistry(params string[] names)
            {
                _names = new HashSet<string>(names, StringComparer.Ordinal);
            }

            public void RegisterFromQuery(string promql) { /* no-op pour les tests */ }

            public bool IsRequestedKey(string metricKey)
            {
                var brace = metricKey.IndexOf('{');
                return _names.Contains(brace < 0 ? metricKey : metricKey[..brace]);
            }

            public IReadOnlyCollection<string> GetRequestedMetricNames() => _names;
        }

        private sealed class TestMetricsScrapingGuard : IMetricsScrapingGuard
        {
            public bool IsEnabled { get; set; }

            public void EnablePromql() => IsEnabled = true;
        }

        private sealed class TestMasterService : IMasterService
        {
            private volatile bool _isMaster;

            public bool IsMaster
            {
                get => _isMaster;
                set => _isMaster = value;
            }
        }

        private sealed class InMemoryDatabaseService : IDatabaseService
        {
            private readonly ConcurrentDictionary<string, byte[]> _storage = new(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, int> _getCounts = new(StringComparer.Ordinal);
            private readonly ConcurrentDictionary<string, int> _setCounts = new(StringComparer.Ordinal);

            public Task<KeyValueCommandResult> SetAsync(
                string key,
                byte[]? value = null,
                long? timeToLiveSeconds = null,
                KeyValueOperation operation = KeyValueOperation.Set,
                long integerDelta = 0,
                decimal floatDelta = 0)
            {
                var result = new KeyValueCommandResult();
                var bytes = value ?? Array.Empty<byte>();
                _storage[key] = bytes;
                _setCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
                result.SetApplied(bytes);
                return Task.FromResult(result);
            }

            public Task HashSetAsync(string key, IDictionary<string, byte[]> values, long? timeToLiveSeconds = null) => throw new NotImplementedException();

            public Task HashSetDeleteAsync(string key, string dictionaryKey = "") => throw new NotImplementedException();

            public Task<IDictionary<string, byte[]>> HashGetAllAsync(string key) => throw new NotImplementedException();

            public Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation, string? newElementId = null) => throw new NotImplementedException();

            public Task<IList<QueueData>?> ListRightPopAsync(string key, string transactionId, int count = 1, IList<string>? reservedIps = null) => throw new NotImplementedException();

            public Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = Int32.MaxValue) => throw new NotImplementedException();

            public Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => throw new NotImplementedException();

            public Task DeleteAsync(string key) => throw new NotImplementedException();

            public Task<byte[]?> GetAsync(string key)
            {
                _getCounts.AddOrUpdate(key, 1, static (_, count) => count + 1);
                _storage.TryGetValue(key, out var value);
                return Task.FromResult<byte[]?>(value?.ToArray());
            }

            public bool TryGetRaw(string key, out byte[] value)
            {
                if (_storage.TryGetValue(key, out var stored))
                {
                    value = stored;
                    return true;
                }

                value = [];
                return false;
            }

            public int GetCount(string key)
                => _getCounts.TryGetValue(key, out var count) ? count : 0;

            public int SetCount(string key)
                => _setCounts.TryGetValue(key, out var count) ? count : 0;
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

            // SlimFaas pods used by the deployment model.
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

        private sealed class DelegateHttpHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => send(request, cancellationToken);
        }

        private sealed class NonSeekableChunkedStream(byte[] content, int maxChunkSize) : Stream
        {
            private int _position;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = Math.Min(Math.Min(count, maxChunkSize), content.Length - _position);
                if (read <= 0)
                    return 0;
                content.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
                _position += read;
                return read;
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = Math.Min(Math.Min(buffer.Length, maxChunkSize), content.Length - _position);
                if (read <= 0)
                    return ValueTask.FromResult(0);
                content.AsMemory(_position, read).CopyTo(buffer);
                _position += read;
                return ValueTask.FromResult(read);
            }

            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }

        private static InMemoryMetricsStore CreateStore(
            out TestRequestedMetricsRegistry registry,
            params string[] requestedMetricNames)
        {
            registry = new TestRequestedMetricsRegistry(requestedMetricNames);
            return new InMemoryMetricsStore(registry, retentionSeconds: 3600);
        }

        private static MetricsScrapingWorker NewWorker(
            DeploymentsInformations deployments,
            HttpClient httpClient,
            bool isMaster,
            InMemoryMetricsStore store,
            IRequestedMetricsRegistry requestedMetricsRegistry,
            InMemoryDatabaseService? database = null,
            bool scrapingEnabled = true,
            int delayMs = 10_000,
            MetricsScrapingOptions? metricsScrapingOptions = null,
            IMasterService? masterService = null) // le scraping se fait avant le premier Delay
        {
            // IReplicasService
            var replicas = new Mock<IReplicasService>();
            replicas.SetupGet(r => r.Deployments).Returns(deployments);

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

            var db = database ?? new InMemoryDatabaseService();

            // IOptions<SlimFaasOptions>
            var slimFaasOptions = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                Namespace = "ns",
                BaseSlimDataUrl = "http://{pod_name}.{service_name}.{namespace}.svc:3262",
                MetricsScraping = metricsScrapingOptions ?? new MetricsScrapingOptions()
            });

            return new MetricsScrapingWorker(
                replicasService: replicas.Object,
                masterService: masterService ?? new TestMasterService { IsMaster = isMaster },
                httpClientFactory: httpFactory.Object,
                metricsStore: store,
                databaseService: db,
                slimDataStatus: status.Object,
                scrapingGuard: guard,
                requestedMetricsRegistry: requestedMetricsRegistry,
                logger: logger,
                slimFaasOptions: slimFaasOptions,
                delay: delayMs
            );
        }

        private static async Task StartRunOnceAndStopAsync(BackgroundService svc, int settleMs = 250)
        {
            await svc.StartAsync(CancellationToken.None);
            await Task.Delay(settleMs);
            await svc.StopAsync(CancellationToken.None);
        }

        private static async Task WaitUntilAsync(
            Func<bool> condition,
            TimeSpan timeout)
        {
            var deadline = DateTimeOffset.UtcNow + timeout;
            while (!condition())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                    throw new TimeoutException("The expected worker condition was not reached.");

                await Task.Delay(20);
            }
        }

        // --- Tests ---

        [Fact]
        public async Task Leader_Scrapes_And_Stores_Metrics()
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
                        metric_unrequested 999
                        metric_nan NaN
                        """;

            var body2 = """
                        metric_one 3
                        metric_two{label="a"} 4.5
                        metric_unrequested 999
                        metric_inf +Inf
                        metric_ninf -Inf
                        """;

            var handler = new MapHttpHandler(new[]
            {
                (depAUrls[0], HttpStatusCode.OK, body1),
                (depAUrls[1], HttpStatusCode.OK, body2)
            });
            var http = new HttpClient(handler);

            var store = CreateStore(out var registry, "metric_one", "metric_two");
            var db = new InMemoryDatabaseService();
            var worker = NewWorker(deployments, http, isMaster: true, store: store,
                requestedMetricsRegistry: registry, database: db);

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

            // Et le snapshot doit être persisté en base
            Assert.True(db.TryGetRaw("metrics:store", out var bytes));
            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);
            Assert.True(db.TryGetRaw("metrics:store:version", out var version));
            Assert.Equal(16, version.Length);
        }

        [Fact]
        public async Task Follower_Does_Not_Scrape()
        {
            var deployments = BuildDeploymentsForScrape();

            var handler = new MapHttpHandler(Array.Empty<(string, HttpStatusCode, string)>());
            var http = new HttpClient(handler);

            // Un follower hydrate éventuellement le store, mais ne scrape jamais.
            var store = CreateStore(out var registry, "metric_one");
            var db = new InMemoryDatabaseService();
            var worker = NewWorker(deployments, http, isMaster: false, store: store,
                requestedMetricsRegistry: registry, database: db);

            await StartRunOnceAndStopAsync(worker);

            var snapshot = store.Snapshot();
            Assert.True(snapshot.Count == 0); // rien scrapé et rien à hydrater
        }

        [Fact]
        public async Task Follower_StartsScrapingAfterBecomingLeader()
        {
            var deployments = BuildDeploymentsForScrape();
            var requestCount = 0;
            var handler = new DelegateHttpHandler((_, _) =>
            {
                Interlocked.Increment(ref requestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("metric_one 9", Encoding.UTF8, "text/plain")
                });
            });
            using var http = new HttpClient(handler);
            var store = CreateStore(out var registry, "metric_one");
            var persistedStore = CreateStore(out _, "metric_one");
            var persistedTimestamp = DateTimeOffset.UtcNow.AddSeconds(-10).ToUnixTimeSeconds();
            persistedStore.Add(
                persistedTimestamp,
                "dep-a",
                "10.1.0.1",
                new Dictionary<string, double> { ["metric_one"] = 3 });
            var database = new InMemoryDatabaseService();
            await database.SetAsync(
                "metrics:store",
                MemoryPack.MemoryPackSerializer.Serialize(persistedStore.CreateRecord()));
            await database.SetAsync("metrics:store:version", Guid.NewGuid().ToByteArray());
            var master = new TestMasterService();
            var worker = NewWorker(
                deployments,
                http,
                isMaster: false,
                store,
                registry,
                database,
                delayMs: 2_000,
                masterService: master);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(
                    () => store.LatestTimestamp == persistedTimestamp,
                    TimeSpan.FromSeconds(1));
                Assert.Equal(0, Volatile.Read(ref requestCount));

                master.IsMaster = true;
                await WaitUntilAsync(
                    () => Volatile.Read(ref requestCount) > 0,
                    TimeSpan.FromSeconds(2));

                var snapshot = store.Snapshot();
                Assert.Contains(persistedTimestamp, snapshot.Keys);
                Assert.True(snapshot.Count >= 2);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
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

            var store = CreateStore(out var registry, "metric_ok");
            var db = new InMemoryDatabaseService();
            var worker = NewWorker(deployments, http, isMaster: true, store: store,
                requestedMetricsRegistry: registry, database: db);

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
        public async Task ContentLengthAboveLimitIsRejectedBeforeParsing()
        {
            var deployments = BuildDeploymentsForScrape();
            var handler = new DelegateHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("metric_one 123456789", Encoding.UTF8, "text/plain")
            }));
            using var http = new HttpClient(handler);
            var store = CreateStore(out var registry, "metric_one");
            var options = new MetricsScrapingOptions
            {
                MaxResponseBytes = 8,
                MaxLineBytes = 8,
                MaxSelectedSeriesPerTarget = 10,
                RequestTimeoutSeconds = 10
            };
            var worker = NewWorker(
                deployments,
                http,
                isMaster: true,
                store,
                registry,
                metricsScrapingOptions: options);

            await StartRunOnceAndStopAsync(worker);

            Assert.Empty(store.Snapshot());
        }

        [Fact]
        public async Task ChunkedResponseWithoutContentLengthIsStreamedAndStored()
        {
            var deployments = BuildDeploymentsForScrape();
            var payload = Encoding.UTF8.GetBytes("metric_one{source=\"chunked\"} 7\nmetric_unused 9\n");
            var handler = new DelegateHttpHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new NonSeekableChunkedStream(payload, maxChunkSize: 2))
                };
                Assert.Null(response.Content.Headers.ContentLength);
                return Task.FromResult(response);
            });
            using var http = new HttpClient(handler);
            var store = CreateStore(out var registry, "metric_one");
            var worker = NewWorker(
                deployments,
                http,
                isMaster: true,
                store,
                registry);

            await StartRunOnceAndStopAsync(worker);

            var podMetrics = store.Snapshot().Values.Single()["dep-a"];
            Assert.Equal(2, podMetrics.Count);
            Assert.All(podMetrics.Values, metrics =>
            {
                var metric = Assert.Single(metrics);
                Assert.Equal("metric_one{source=\"chunked\"}", metric.Key);
                Assert.Equal(7, metric.Value);
            });
        }

        [Fact]
        public async Task PerTargetTimeoutCancelsStreamingRequest()
        {
            var deployments = BuildDeploymentsForScrape();
            var cancellations = 0;
            var handler = new DelegateHttpHandler(async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancellations);
                    throw;
                }
            });
            using var http = new HttpClient(handler);
            var store = CreateStore(out var registry, "metric_one");
            var options = new MetricsScrapingOptions
            {
                MaxResponseBytes = 1024,
                MaxLineBytes = 256,
                MaxSelectedSeriesPerTarget = 10,
                RequestTimeoutSeconds = 1
            };
            var worker = NewWorker(
                deployments,
                http,
                isMaster: true,
                store,
                registry,
                metricsScrapingOptions: options);

            await StartRunOnceAndStopAsync(worker, settleMs: 1_250);

            Assert.True(cancellations >= 1);
            Assert.Empty(store.Snapshot());
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

            var store = CreateStore(out var registry, "metric_one", "metric_two");
            var db = new InMemoryDatabaseService();
            var worker = NewWorker(deployments, http, isMaster: true, store: store,
                requestedMetricsRegistry: registry, database: db);

            await StartRunOnceAndStopAsync(worker);

            var snapshot = store.Snapshot();
            var depMap = snapshot.Values.Single();
            var podMap = depMap["dep-a"];
            var m = podMap["10.1.0.1"];

            Assert.Equal(2, m.Count);
            Assert.Equal(1.0, m["metric_one"], 6);
            Assert.Equal(2.5, m[@"metric_two{label=""a""}"], 6);
        }

        [Fact]
        public async Task Follower_Hydrates_From_Database()
        {
            var deployments = BuildDeploymentsForScrape();

            // 1) Leader: scrape + persist
            var urls = deployments.GetMetricsTargets();
            var depAUrls = urls["dep-a"].OrderBy(x => x).ToArray();

            var body1 = """
                        metric_one 1
                        metric_two{label="a"} 2.5
                        """;

            var body2 = """
                        metric_one 3
                        metric_two{label="a"} 4.5
                        """;

            var handler1 = new MapHttpHandler(new[]
            {
                (depAUrls[0], HttpStatusCode.OK, body1),
                (depAUrls[1], HttpStatusCode.OK, body2)
            });
            var http1 = new HttpClient(handler1);

            var db = new InMemoryDatabaseService();

            var leaderStore = CreateStore(out var leaderRegistry, "metric_one", "metric_two");
            var leaderWorker = NewWorker(
                deployments,
                http1,
                isMaster: true,
                store: leaderStore,
                requestedMetricsRegistry: leaderRegistry,
                database: db);

            await StartRunOnceAndStopAsync(leaderWorker);

            var leaderSnapshot = leaderStore.Snapshot();
            Assert.NotEmpty(leaderSnapshot);
            Assert.True(db.TryGetRaw("metrics:store", out var bytes));
            Assert.NotNull(bytes);
            Assert.NotEmpty(bytes);

            // 2) Follower: do not scrape, hydrate from the persisted backup.
            var handler2 = new MapHttpHandler(Array.Empty<(string, HttpStatusCode, string)>());
            var http2 = new HttpClient(handler2);

            var followerStore = CreateStore(out var followerRegistry, "metric_one", "metric_two");
            var followerWorker = NewWorker(
                deployments,
                http2,
                isMaster: false,
                store: followerStore,
                requestedMetricsRegistry: followerRegistry,
                database: db);

            await StartRunOnceAndStopAsync(followerWorker);

            var followerSnapshot = followerStore.Snapshot();
            Assert.NotEmpty(followerSnapshot);

            // On vérifie qu'il a bien les métriques attendues (hydrate OK)
            var depMap = followerSnapshot.Values.Single();
            var podMap = depMap["dep-a"];

            Assert.True(podMap.ContainsKey("10.1.0.1"));
            Assert.True(podMap.ContainsKey("10.1.0.2"));

            var m1 = podMap["10.1.0.1"];
            var m2 = podMap["10.1.0.2"];

            Assert.Equal(1.0, m1["metric_one"], 6);
            Assert.Equal(2.5, m1[@"metric_two{label=""a""}"], 6);

            Assert.Equal(3.0, m2["metric_one"], 6);
            Assert.Equal(4.5, m2[@"metric_two{label=""a""}"], 6);
        }

        [Fact]
        public async Task Leader_PersistsAtMostOnceWithinThirtySeconds()
        {
            var deployments = BuildDeploymentsForScrape();
            var requestCount = 0;
            var handler = new DelegateHttpHandler((_, _) =>
            {
                Interlocked.Increment(ref requestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("metric_one 1", Encoding.UTF8, "text/plain")
                });
            });
            using var http = new HttpClient(handler);
            var store = CreateStore(out var registry, "metric_one");
            var db = new InMemoryDatabaseService();
            var worker = NewWorker(
                deployments,
                http,
                isMaster: true,
                store,
                registry,
                database: db,
                delayMs: 10);

            await StartRunOnceAndStopAsync(worker, settleMs: 150);

            Assert.True(requestCount > 2);
            Assert.Equal(1, db.SetCount("metrics:store"));
            Assert.Equal(1, db.SetCount("metrics:store:version"));
        }

        [Fact]
        public async Task ConfiguredScrapeIntervalControlsLeaderCadence()
        {
            var deployments = BuildDeploymentsForScrape();
            var requestCount = 0;
            var handler = new DelegateHttpHandler((_, _) =>
            {
                Interlocked.Increment(ref requestCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("metric_one 1", Encoding.UTF8, "text/plain")
                });
            });
            using var http = new HttpClient(handler);
            var store = CreateStore(out var registry, "metric_one");
            var options = new MetricsScrapingOptions
            {
                ScrapeIntervalMilliseconds = 25
            };
            var worker = NewWorker(
                deployments,
                http,
                isMaster: true,
                store,
                registry,
                delayMs: 0,
                metricsScrapingOptions: options);

            await StartRunOnceAndStopAsync(worker, settleMs: 140);

            // There are two targets per cycle, so at least two completed cycles
            // prove that the configured interval is used instead of the old 5 s.
            Assert.True(Volatile.Read(ref requestCount) >= 4);
        }

        [Fact]
        public async Task Follower_DoesNotReloadPayloadWhenVersionIsUnchanged()
        {
            var deployments = BuildDeploymentsForScrape();
            var sourceStore = CreateStore(out _, "metric_one");
            sourceStore.Add(
                100,
                "dep-a",
                "10.1.0.1",
                new Dictionary<string, double> { ["metric_one"] = 7 });
            var db = new InMemoryDatabaseService();
            await db.SetAsync(
                "metrics:store",
                MemoryPack.MemoryPackSerializer.Serialize(sourceStore.CreateRecord()));
            await db.SetAsync("metrics:store:version", Guid.NewGuid().ToByteArray());

            using var http = new HttpClient(
                new MapHttpHandler(Array.Empty<(string, HttpStatusCode, string)>()));
            var hydratedStore = CreateStore(out var registry, "metric_one");
            var worker = NewWorker(
                deployments,
                http,
                isMaster: false,
                hydratedStore,
                registry,
                database: db);

            await StartRunOnceAndStopAsync(worker, settleMs: 1_250);

            Assert.NotEmpty(hydratedStore.Snapshot());
            Assert.True(db.GetCount("metrics:store:version") >= 2);
            Assert.Equal(1, db.GetCount("metrics:store"));
        }

        [Fact]
        public async Task Follower_ReloadsPayloadWhenVersionChanges()
        {
            var deployments = BuildDeploymentsForScrape();
            var sourceStore = CreateStore(out _, "metric_one");
            sourceStore.Add(
                100,
                "dep-a",
                "10.1.0.1",
                new Dictionary<string, double> { ["metric_one"] = 7 });
            var db = new InMemoryDatabaseService();
            await db.SetAsync(
                "metrics:store",
                MemoryPack.MemoryPackSerializer.Serialize(sourceStore.CreateRecord()));
            await db.SetAsync("metrics:store:version", Guid.NewGuid().ToByteArray());

            using var http = new HttpClient(
                new MapHttpHandler(Array.Empty<(string, HttpStatusCode, string)>()));
            var hydratedStore = CreateStore(out var registry, "metric_one");
            var worker = NewWorker(
                deployments,
                http,
                isMaster: false,
                hydratedStore,
                registry,
                database: db);

            await worker.StartAsync(CancellationToken.None);
            await Task.Delay(100);

            sourceStore.Add(
                105,
                "dep-a",
                "10.1.0.1",
                new Dictionary<string, double> { ["metric_one"] = 9 });
            await db.SetAsync(
                "metrics:store",
                MemoryPack.MemoryPackSerializer.Serialize(sourceStore.CreateRecord()));
            await db.SetAsync("metrics:store:version", Guid.NewGuid().ToByteArray());

            await Task.Delay(1_100);
            await worker.StopAsync(CancellationToken.None);

            Assert.Equal(
                9,
                hydratedStore.Snapshot()[105]["dep-a"]["10.1.0.1"]["metric_one"]);
            Assert.Equal(2, db.GetCount("metrics:store"));
        }

        [Fact]
        public async Task Follower_HydratesLegacyUnversionedRecordOnlyOnce()
        {
            var deployments = BuildDeploymentsForScrape();
            var sourceStore = CreateStore(out _, "metric_one");
            sourceStore.Add(
                100,
                "dep-a",
                "10.1.0.1",
                new Dictionary<string, double> { ["metric_one"] = 7 });
            var db = new InMemoryDatabaseService();
            await db.SetAsync(
                "metrics:store",
                MemoryPack.MemoryPackSerializer.Serialize(sourceStore.CreateRecord()));

            using var http = new HttpClient(
                new MapHttpHandler(Array.Empty<(string, HttpStatusCode, string)>()));
            var hydratedStore = CreateStore(out var registry, "metric_one");
            var worker = NewWorker(
                deployments,
                http,
                isMaster: false,
                hydratedStore,
                registry,
                database: db);

            await StartRunOnceAndStopAsync(worker, settleMs: 1_250);

            Assert.NotEmpty(hydratedStore.Snapshot());
            Assert.True(db.GetCount("metrics:store:version") >= 2);
            Assert.Equal(1, db.GetCount("metrics:store"));
        }
    }
}
