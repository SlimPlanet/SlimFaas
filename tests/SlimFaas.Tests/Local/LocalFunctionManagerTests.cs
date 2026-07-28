using System.Net;
using System.Net.Sockets;
using SlimFaas.Kubernetes;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalFunctionManagerTests
{
    [Fact]
    public async Task Reconcile_PublishesPortExhaustionThenAllocatesAndReleasesPort()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        string statePath = Path.Combine(root, "state");
        Directory.CreateDirectory(root);
        int port = FindAvailablePort();
        var manifest = new LocalManifest
        {
            Name = "function-test",
            Cluster = new LocalClusterManifest
            {
                Nodes = 1,
                GatewayPort = port + 10,
                HttpPortBase = port + 11,
                RaftPortBase = port + 12
            },
            ProcessPorts = new LocalPortRangeManifest { From = port, To = port },
            Functions = new Dictionary<string, LocalFunctionManifest>
            {
                ["test-function"] = new LocalFunctionManifest
                {
                    Command =
                    [
                        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
                        "--info"
                    ],
                    WorkingDirectory = root,
                    Annotations = new Dictionary<string, string>
                    {
                        [FunctionAnnotationNames.Function] = "true",
                        [FunctionAnnotationNames.ReplicasAtStart] = "1",
                        [FunctionAnnotationNames.Scale] = """{"ReplicaMax":1}"""
                    }
                }
            }
        };
        var loaded = new LoadedLocalManifest(
            manifest,
            Path.Combine(root, "manifest.yaml"),
            root,
            statePath,
            IsPersistent: true);

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalFunctionManager(loaded, allocator, state);
            using var external = new TcpListener(IPAddress.Loopback, port);
            external.Start();
            await manager.StartAsync(CancellationToken.None);

            PodInformation exhausted = await WaitForPodAsync(
                manager,
                pod => pod.StartFailureReason == "PortRangeExhausted");
            Assert.Empty(exhausted.Ports!);

            external.Stop();
            PodInformation allocated = await WaitForPodAsync(
                manager,
                pod => pod.Ports?.Contains(port) == true);
            Assert.Equal(port, Assert.Single(allocated.Ports!));

            await manager.ScaleAsync(
                new ReplicaRequest("test-function", "function-test", 0, PodType.Deployment),
                CancellationToken.None);
            await WaitForAsync(() => manager.Snapshot("function-test").Single().Pods.Count == 0);
            await WaitForAsync(() => !state.Allocations.ContainsKey("function:test-function:0"));
            Assert.DoesNotContain("function:test-function:0", state.Allocations.Keys);

            await manager.ScaleAsync(
                new ReplicaRequest("test-function", "function-test", 1, PodType.Deployment),
                CancellationToken.None);
            PodInformation reallocated = await WaitForPodAsync(
                manager,
                pod => pod.Ports?.Contains(port) == true);
            Assert.Equal(port, Assert.Single(reallocated.Ports!));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PodInformation> WaitForPodAsync(
        LocalFunctionManager manager,
        Func<PodInformation, bool> predicate)
    {
        PodInformation? last = null;
        await WaitForAsync(() =>
        {
            last = manager.Snapshot("function-test").Single().Pods.SingleOrDefault();
            return last is not null && predicate(last);
        });
        return last!;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("Condition was not reached before the timeout.");
    }

    private static int FindAvailablePort()
    {
        for (var port = 44000; port < 60000; port += 20)
        {
            if (Enumerable.Range(port, 13).All(LocalManifestLoader.IsTcpPortAvailable))
                return port;
        }

        throw new InvalidOperationException("No free local port range was found for the test.");
    }
}
