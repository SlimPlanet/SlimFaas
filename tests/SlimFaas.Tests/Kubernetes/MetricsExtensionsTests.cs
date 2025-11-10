using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes
{
    public class MetricsExtensionsTests
    {
        private static PodInformation Pod(
            string name,
            string ip,
            string deploymentName,
            IDictionary<string, string>? annotations)
        {
            return new PodInformation(
                Name: name,
                Started: true,
                Ready: true,
                Ip: ip,
                DeploymentName: deploymentName,
                Ports: new List<int> { 5000 }, // ignoré par MetricsExtensions (pas de fallback)
                ResourceVersion: "1")
            {
                Annotations = annotations
            };
        }

        private static IDictionary<string, string> Anns(
            string? scrape = "true",
            string? port = "5000",
            string? path = "/metrics",
            string? scheme = null)
        {
            var d = new Dictionary<string, string>();
            if (scrape is not null) d["prometheus.io/scrape"] = scrape;
            if (port is not null) d["prometheus.io/port"] = port;
            if (path is not null) d["prometheus.io/path"] = path;
            if (!string.IsNullOrWhiteSpace(scheme)) d["prometheus.io/scheme"] = scheme!;
            return d;
        }

        [Fact]
        public void GetMetricsTargets_Pod_WithValidAnnotations_ReturnsSingleUrl()
        {
            var pod = Pod("p1", "10.0.0.1", "dep-a", Anns());
            var urls = pod.GetMetricsTargets();

            Assert.Single(urls);
            Assert.Equal("http://10.0.0.1:5000/metrics", urls[0]);
        }

        [Fact]
        public void GetMetricsTargets_Pod_WithHttpsScheme_UsesHttps()
        {
            var pod = Pod("p1", "10.0.0.2", "dep-a", Anns(scheme: "https"));
            var urls = pod.GetMetricsTargets();

            Assert.Single(urls);
            Assert.Equal("https://10.0.0.2:5000/metrics", urls[0]);
        }

        [Fact]
        public void GetMetricsTargets_Pod_PathWithoutLeadingSlash_IsNormalized()
        {
            var pod = Pod("p1", "10.0.0.3", "dep-a", Anns(path: "metrics"));
            var urls = pod.GetMetricsTargets();

            Assert.Single(urls);
            Assert.Equal("http://10.0.0.3:5000/metrics", urls[0]);
        }

        [Theory]
        [InlineData("false")]
        [InlineData("0")]
        [InlineData("no")]
        public void GetMetricsTargets_Pod_ScrapeNotEnabled_ReturnsEmpty(string scrapeValue)
        {
            var pod = Pod("p1", "10.0.0.4", "dep-a", Anns(scrape: scrapeValue));
            var urls = pod.GetMetricsTargets();

            Assert.Empty(urls);
        }

        [Fact]
        public void GetMetricsTargets_Pod_NoPortAnnotation_ReturnsEmpty()
        {
            var anns = Anns(port: null);
            var pod = Pod("p1", "10.0.0.5", "dep-a", anns);
            var urls = pod.GetMetricsTargets();

            Assert.Empty(urls);
        }

        [Fact]
        public void GetMetricsTargets_Pod_InvalidPortAnnotation_ReturnsEmpty()
        {
            var anns = Anns(port: "not-a-number");
            var pod = Pod("p1", "10.0.0.6", "dep-a", anns);
            var urls = pod.GetMetricsTargets();

            Assert.Empty(urls);
        }

        [Fact]
        public void GetMetricsTargets_Pod_EmptyIp_ReturnsEmpty()
        {
            var pod = Pod("p1", "", "dep-a", Anns());
            var urls = pod.GetMetricsTargets();

            Assert.Empty(urls);
        }

        [Fact]
        public void GetMetricsTargets_Deployments_MapContainsFunctionNamesAndSlimFaasOnly()
        {
            // dep-a avec 2 pods (1 url chacun)
            var depAPod1 = Pod("a-p1", "10.1.0.1", "dep-a", Anns(port: "5001"));
            var depAPod2 = Pod("a-p2", "10.1.0.2", "dep-a", Anns(port: "5002"));

            // dep-b avec 1 pod, scheme https + path custom
            var depBPod1 = Pod("b-p1", "10.2.0.1", "dep-b", Anns(port: "9090", path: "/m", scheme: "https"));

            // slimfaas pods
            var sfPod1 = Pod("sf-p1", "10.9.0.1", "slimfaas", Anns(port: "2112", path: "/metrics"));
            var sfPod2 = Pod("sf-p2", "10.9.0.2", "slimfaas", Anns(port: "2113", path: "metrics"));

            var depA = new DeploymentInformation(
                Deployment: "dep-a",
                Namespace: "ns",
                Pods: new List<PodInformation> { depAPod1, depAPod2 },
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

            var depB = depA with
            {
                Deployment = "dep-b",
                Pods = new List<PodInformation> { depBPod1 }
            };

            var infos = new DeploymentsInformations(
                Functions: new List<DeploymentInformation> { depA, depB },
                SlimFaas: new SlimFaasDeploymentInformation(
                    Replicas: 2,
                    Pods: new List<PodInformation> { sfPod1, sfPod2 }
                ),
                Pods: new List<PodInformation>() // aucun "other" attendu
            );

            var map = infos.GetMetricsTargets();

            Assert.True(map.ContainsKey("dep-a"));
            Assert.True(map.ContainsKey("dep-b"));
            Assert.True(map.ContainsKey("slimfaas"));
            Assert.False(map.ContainsKey("other")); // pas de groupe "other" dans l'implé sans fallback

            // dep-a URLs
            var aUrls = map["dep-a"].OrderBy(s => s).ToList();
            Assert.Equal(2, aUrls.Count);
            Assert.Equal("http://10.1.0.1:5001/metrics", aUrls[0]);
            Assert.Equal("http://10.1.0.2:5002/metrics", aUrls[1]);

            // dep-b URL
            var bUrls = map["dep-b"];
            Assert.Single(bUrls);
            Assert.Equal("https://10.2.0.1:9090/m", bUrls[0]);

            // slimfaas URLs (normalisation du path sans slash pour sf-p2)
            var sfUrls = map["slimfaas"].OrderBy(s => s).ToList();
            Assert.Equal(2, sfUrls.Count);
            Assert.Equal("http://10.9.0.1:2112/metrics", sfUrls[0]);
            Assert.Equal("http://10.9.0.2:2113/metrics", sfUrls[1]);
        }

        [Fact]
        public void GetMetricsTargets_Deployments_IgnoresPodsWithoutAnnotationsOrMissingPort()
        {
            var good = Pod("p-good", "10.3.0.1", "dep-x", Anns(port: "8080", path: "/metrics"));
            var noScrape = Pod("p-noscrape", "10.3.0.2", "dep-x", Anns(scrape: "false"));
            var noPort = Pod("p-noport", "10.3.0.3", "dep-x", Anns(port: null));
            var badPort = Pod("p-badport", "10.3.0.4", "dep-x", Anns(port: "abc"));

            var dep = new DeploymentInformation(
                Deployment: "dep-x",
                Namespace: "ns",
                Pods: new List<PodInformation> { good, noScrape, noPort, badPort },
                Configuration: new SlimFaasConfiguration(),
                Replicas: 1,
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

            var infos = new DeploymentsInformations(
                Functions: new List<DeploymentInformation> { dep },
                SlimFaas: new SlimFaasDeploymentInformation(0, new List<PodInformation>()),
                Pods: new List<PodInformation>());

            var map = infos.GetMetricsTargets();
            var urls = map["dep-x"];

            Assert.Single(urls);
            Assert.Equal("http://10.3.0.1:8080/metrics", urls[0]);
        }
    }
}
