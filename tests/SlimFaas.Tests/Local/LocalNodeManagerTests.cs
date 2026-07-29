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

            Assert.Equal(3, pods.Count);
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
