using SlimFaas.Kubernetes;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalNodeManagerTests
{
    [Fact]
    public async Task Snapshot_AdvertisesEachNodeMetricsEndpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        var manifest = new LocalManifest
        {
            Name = "node-metrics-test",
            Cluster = new LocalClusterManifest
            {
                Nodes = 3,
                NodeHttpPortBase = 31021,
                RaftPortBase = 3362
            }
        };
        var loaded = new LoadedLocalManifest(
            manifest,
            Path.Combine(root, "manifest.yaml"),
            root,
            Path.Combine(root, "state"),
            IsPersistent: true);

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            await using var manager = new LocalNodeManager(
                loaded,
                state,
                new Uri("http://127.0.0.1:12345"),
                "test-token");

            IList<PodInformation> pods = manager.Snapshot();
            IReadOnlyDictionary<string, string> environment = manager.BuildNodeEnvironment(0);

            Assert.Equal(3, pods.Count);
            Assert.Equal("slimfaas-0", environment["HOSTNAME"]);
            Assert.Equal("false", environment["OpenTelemetry__Enable"]);
            Assert.Equal("false", environment["OpenTelemetry__EnableConsoleExporter"]);
            Assert.All(
                new[]
                {
                    "Logging__LogLevel__Default",
                    "Logging__LogLevel__Microsoft.AspNetCore",
                    "Logging__LogLevel__SlimFaas",
                    "Logging__LogLevel__SlimData",
                    "Logging__LogLevel__DotNext",
                    "Logging__LogLevel__DotNext.Net.Cluster",
                    "Logging__LogLevel__DotNext.Net.Cluster.Consensus.Raft"
                },
                key => Assert.Equal("Error", environment[key]));
            Assert.Equal(
                [
                    "http://127.0.0.1:31021/metrics",
                    "http://127.0.0.1:31022/metrics",
                    "http://127.0.0.1:31023/metrics"
                ],
                pods.SelectMany(pod => (pod with { Ready = true }).GetMetricsTargets()).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
