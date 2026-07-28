using SlimFaas.Local;
using System.Net;
using System.Net.Sockets;

namespace SlimFaas.Tests.Local;

public sealed class LocalPortAllocatorTests
{
    [Fact]
    public void Reserve_PersistsIdentityAndReusesReleasedPort()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            int first = FindAvailableRangeStart();
            var manifest = new LocalManifest
            {
                Name = "port-test",
                Cluster = new LocalClusterManifest
                {
                    Nodes = 1,
                    GatewayPort = first + 10,
                    HttpPortBase = first + 11,
                    RaftPortBase = first + 12
                },
                ProcessPorts = new LocalPortRangeManifest { From = first, To = first + 2 },
                State = new LocalStateManifest { Mode = "persistent", Directory = root }
            };
            var loaded = new LoadedLocalManifest(
                manifest,
                Path.Combine(root, "manifest.yaml"),
                root,
                root,
                IsPersistent: true);
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);

            int replicaZero = Assert.IsType<int>(allocator.Reserve("function:test:0"));
            int replicaOne = Assert.IsType<int>(allocator.Reserve("function:test:1"));
            Assert.NotEqual(replicaZero, replicaOne);
            Assert.Equal(replicaZero, allocator.Reserve("function:test:0"));

            allocator.Release("function:test:0");
            Assert.Equal(replicaZero, allocator.Reserve("function:test:2"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reserve_RetriesAfterAnExternalProcessReleasesTheOnlyPort()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int port = FindAvailableRangeStart();
        try
        {
            var manifest = new LocalManifest
            {
                Name = "exhaustion-test",
                Cluster = new LocalClusterManifest
                {
                    Nodes = 1,
                    GatewayPort = port + 10,
                    HttpPortBase = port + 11,
                    RaftPortBase = port + 12
                },
                ProcessPorts = new LocalPortRangeManifest { From = port, To = port }
            };
            var loaded = new LoadedLocalManifest(
                manifest,
                Path.Combine(root, "manifest.yaml"),
                root,
                root,
                IsPersistent: true);
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            using var external = new TcpListener(IPAddress.Loopback, port);
            external.Start();

            Assert.Null(allocator.Reserve("function:test:0"));
            external.Stop();
            Assert.Equal(port, allocator.Reserve("function:test:0"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

<<<<<<< HEAD
    [Fact]
    public void ReserveFixed_RejectsConflictsAndAllowsPortsOutsideTheDynamicRange()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int rangeStart = FindAvailableRangeStart();
        int fixedPort = rangeStart + 5;
        try
        {
            LoadedLocalManifest loaded = CreateLoadedManifest(root, rangeStart);
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);

            Assert.Equal(fixedPort, allocator.ReserveFixed("process:frontend", fixedPort));
            Assert.Null(allocator.ReserveFixed("process:watcher", fixedPort));
            Assert.Null(allocator.ReserveFixed(
                "process:gateway-conflict",
                loaded.Manifest.Cluster.GatewayPort));

            allocator.Release("process:frontend");
            using var external = new TcpListener(IPAddress.Loopback, fixedPort);
            external.Start();
            Assert.Null(allocator.ReserveFixed("process:frontend", fixedPort));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Prune_RemovesAllocationsForDeletedFunctionsAndProcesses()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int rangeStart = FindAvailableRangeStart();
        try
        {
            LoadedLocalManifest loaded = CreateLoadedManifest(root, rangeStart);
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            Assert.NotNull(allocator.Reserve("function:active:0"));
            Assert.NotNull(allocator.Reserve("function:removed:0"));
            Assert.NotNull(allocator.ReserveFixed("process:removed", rangeStart + 5));

            allocator.Prune(new HashSet<string>(["function:active:0"], StringComparer.Ordinal));

            Assert.Equal(["function:active:0"], state.Allocations.Keys);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReserveFixed_TakesPriorityOverADynamicAllocationFromAnEarlierRun()
    {
        string root = Path.Combine(Path.GetTempPath(), "SlimFaas.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int rangeStart = FindAvailableRangeStart();
        try
        {
            LoadedLocalManifest loaded = CreateLoadedManifest(root, rangeStart);
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var previousRun = new LocalPortAllocator(loaded, state);
            Assert.Equal(rangeStart, previousRun.Reserve("function:test:0"));

            var restarted = new LocalPortAllocator(loaded, state);
            Assert.Equal(rangeStart, restarted.ReserveFixed("process:frontend", rangeStart));

            Assert.DoesNotContain("function:test:0", state.Allocations.Keys);
            Assert.Equal(rangeStart, state.GetAllocatedPort("process:frontend"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static LoadedLocalManifest CreateLoadedManifest(string root, int rangeStart)
    {
        var manifest = new LocalManifest
        {
            Name = "port-test",
            Cluster = new LocalClusterManifest
            {
                Nodes = 1,
                GatewayPort = rangeStart + 10,
                HttpPortBase = rangeStart + 11,
                RaftPortBase = rangeStart + 12
            },
            ProcessPorts = new LocalPortRangeManifest { From = rangeStart, To = rangeStart + 2 },
            State = new LocalStateManifest { Mode = "persistent", Directory = root }
        };
        return new LoadedLocalManifest(
            manifest,
            Path.Combine(root, "manifest.yaml"),
            root,
            root,
            IsPersistent: true);
    }

=======
>>>>>>> origin/main
    private static int FindAvailableRangeStart()
    {
        for (var port = 42000; port < 60000; port += 20)
        {
            if (Enumerable.Range(port, 13).All(LocalManifestLoader.IsTcpPortAvailable))
                return port;
        }

        throw new InvalidOperationException("No free local port range was found for the test.");
    }
}
