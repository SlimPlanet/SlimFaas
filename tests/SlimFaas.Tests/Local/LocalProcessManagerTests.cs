using System.Net;
using System.Net.Sockets;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalProcessManagerTests
{
    [Fact]
    public void RetryDelay_IsExponentialAndCappedAtThirtySeconds()
    {
        Assert.Equal(
            [1, 2, 4, 8, 16, 30, 30],
            Enumerable.Range(1, 7)
                .Select(failure => (int)LocalProcessManager.RetryDelay(failure).TotalSeconds));
    }

    [Fact]
    public async Task Never_RunsOnceInjectsAndExpandsPortAndWritesPrefixedLog()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = PortEchoCommand(),
                WorkingDirectory = ".",
                Environment = new Dictionary<string, string>
                {
                    ["PROCESS_TEST_VALUE"] = "port-{port}"
                },
                Port = "auto",
                RestartPolicy = "NeVeR"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            LocalProcessStatus status;
            await using (var manager = new LocalProcessManager(loaded, allocator, state))
            {
                manager.PreparePorts();
                int allocatedPort = Assert.IsType<int>(manager.Snapshot().Single().Port);
                await manager.StartAsync(CancellationToken.None);

                status = await WaitForStatusAsync(manager, item => item.Terminal);
                Assert.Equal(1, status.StartCount);
                Assert.Equal(0, status.LastExitCode);

                string log = await WaitForLogAsync(
                    Path.Combine(state.LogsDirectory, "process-frontend.log"),
                    allocatedPort.ToString());
                Assert.Contains("[process/frontend]", log);
                Assert.Contains($"PORT={allocatedPort}", log);
                Assert.Contains(allocatedPort.ToString(), log);
            }

            Assert.DoesNotContain("process:frontend", state.Allocations.Keys);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task OnFailure_RestartsNonZeroExitAndKeepsItsAutomaticPort()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [DotnetExecutable(), "--definitely-not-a-dotnet-option"],
                Port = "auto",
                RestartPolicy = "onFailure"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            manager.PreparePorts();
            int allocatedPort = Assert.IsType<int>(manager.Snapshot().Single().Port);
            await manager.StartAsync(CancellationToken.None);

            LocalProcessStatus status = await WaitForStatusAsync(
                manager,
                item => item.StartCount >= 2);

            Assert.False(status.Terminal);
            Assert.Equal(allocatedPort, status.Port);
            Assert.Equal(allocatedPort, state.GetAllocatedPort("process:frontend"));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task OnFailure_DoesNotRestartAZeroExit()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [DotnetExecutable(), "--version"],
                RestartPolicy = "onFailure"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            manager.PreparePorts();
            await manager.StartAsync(CancellationToken.None);

            LocalProcessStatus status = await WaitForStatusAsync(manager, item => item.Terminal);

            Assert.Equal(1, status.StartCount);
            Assert.Equal(0, status.LastExitCode);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Never_DoesNotRetryALaunchFailure()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [Path.Combine(root, "missing-executable")],
                RestartPolicy = "never"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            manager.PreparePorts();
            await manager.StartAsync(CancellationToken.None);

            LocalProcessStatus status = await WaitForStatusAsync(manager, item => item.Terminal);

            Assert.Equal(1, status.AttemptCount);
            Assert.Equal(0, status.StartCount);
            Assert.Equal(-1, status.LastExitCode);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task OnFailure_RetriesALaunchFailure()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [Path.Combine(root, "missing-executable")],
                RestartPolicy = "onFailure"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            manager.PreparePorts();
            await manager.StartAsync(CancellationToken.None);

            LocalProcessStatus status = await WaitForStatusAsync(
                manager,
                item => item.AttemptCount >= 2);

            Assert.False(status.Terminal);
            Assert.Equal(0, status.StartCount);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Always_RestartsAZeroExit()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [DotnetExecutable(), "--version"],
                RestartPolicy = "always"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            manager.PreparePorts();
            await manager.StartAsync(CancellationToken.None);

            LocalProcessStatus status = await WaitForStatusAsync(
                manager,
                item => item.StartCount >= 2);

            Assert.False(status.Terminal);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Cancellation_DisablesFurtherRestarts()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [DotnetExecutable(), "--version"],
                RestartPolicy = "always"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            using var stopping = new CancellationTokenSource();
            manager.PreparePorts();
            await manager.StartAsync(stopping.Token);
            await WaitForStatusAsync(manager, item => item.StartCount == 1);

            await stopping.CancelAsync();
            await Task.Delay(1500);

            Assert.Equal(1, manager.Snapshot().Single().StartCount);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task Start_LaunchesAllConfiguredProcessesWithoutWaitingForTheirExit()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LocalProcessManifest first = LongRunningProcess();
        LocalProcessManifest second = LongRunningProcess();
        var processes = new Dictionary<string, LocalProcessManifest>(StringComparer.OrdinalIgnoreCase)
        {
            ["frontend"] = first,
            ["watcher"] = second
        };
        LoadedLocalManifest loaded = CreateLoadedManifest(root, rangeStart, processes);

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            manager.PreparePorts();
            await manager.StartAsync(CancellationToken.None);

            await WaitForAsync(() =>
                manager.Snapshot().Count == 2 &&
                manager.Snapshot().All(status => status.Running));
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task PreparePorts_ReservesAutomaticPortsBeforeFunctionsAndReleasesAtStop()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = LongRunningProcess().Command,
                Port = "auto",
                RestartPolicy = "never"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            int processPort;
            await using (var manager = new LocalProcessManager(loaded, allocator, state))
            {
                manager.PreparePorts();
                processPort = Assert.IsType<int>(manager.Snapshot().Single().Port);
                int functionPort = Assert.IsType<int>(allocator.Reserve("function:test:0"));
                Assert.NotEqual(processPort, functionPort);
            }

            Assert.DoesNotContain("process:frontend", state.Allocations.Keys);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public async Task AutomaticPort_IsRetriedAfterPoolExhaustion()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = LongRunningProcess().Command,
                Port = "auto",
                RestartPolicy = "always"
            });
        loaded.Manifest.ProcessPorts.To = rangeStart;

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            await using var manager = new LocalProcessManager(loaded, allocator, state);
            using var external = new TcpListener(IPAddress.Loopback, rangeStart);
            external.Start();
            manager.PreparePorts();
            Assert.Null(manager.Snapshot().Single().Port);
            await manager.StartAsync(CancellationToken.None);

            await Task.Delay(750);
            Assert.Equal(0, manager.Snapshot().Single().StartCount);

            external.Stop();
            LocalProcessStatus status = await WaitForStatusAsync(
                manager,
                item => item.StartCount == 1);

            Assert.Equal(rangeStart, status.Port);
            Assert.True(status.Running);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    [Fact]
    public void PreparePorts_RejectsAnOccupiedFixedPort()
    {
        string root = CreateTemporaryRoot();
        int rangeStart = FindAvailableRangeStart();
        int fixedPort = rangeStart + 5;
        LoadedLocalManifest loaded = CreateLoadedManifest(
            root,
            rangeStart,
            new LocalProcessManifest
            {
                Command = [DotnetExecutable(), "--version"],
                Port = fixedPort.ToString(),
                RestartPolicy = "never"
            });

        try
        {
            using LocalStateStore state = LocalStateStore.Open(loaded, clean: false);
            var allocator = new LocalPortAllocator(loaded, state);
            using var external = new TcpListener(IPAddress.Loopback, fixedPort);
            external.Start();
            using var manager = new AsyncDisposableAdapter(
                new LocalProcessManager(loaded, allocator, state));

            LocalManifestException error = Assert.Throws<LocalManifestException>(
                manager.Value.PreparePorts);

            Assert.Contains($"port {fixedPort} is already in use", error.Message);
        }
        finally
        {
            DeleteTemporaryRoot(root);
        }
    }

    private static LoadedLocalManifest CreateLoadedManifest(
        string root,
        int rangeStart,
        LocalProcessManifest process)
        => CreateLoadedManifest(
            root,
            rangeStart,
            new Dictionary<string, LocalProcessManifest>(StringComparer.OrdinalIgnoreCase)
            {
                ["frontend"] = process
            });

    private static LoadedLocalManifest CreateLoadedManifest(
        string root,
        int rangeStart,
        Dictionary<string, LocalProcessManifest> processes)
    {
        string statePath = Path.Combine(root, "state");
        var manifest = new LocalManifest
        {
            Name = "process-test",
            Cluster = new LocalClusterManifest
            {
                Nodes = 1,
                EntrypointPort = rangeStart + 10,
                NodeHttpPortBase = rangeStart + 11,
                RaftPortBase = rangeStart + 12
            },
            ProcessPorts = new LocalPortRangeManifest
            {
                From = rangeStart,
                To = rangeStart + 2
            },
            State = new LocalStateManifest
            {
                Mode = "persistent",
                Directory = statePath
            },
            Processes = processes
        };
        return new LoadedLocalManifest(
            manifest,
            Path.Combine(root, "manifest.yaml"),
            root,
            statePath,
            IsPersistent: true);
    }

    private static List<string> PortEchoCommand()
        => OperatingSystem.IsWindows()
            ? ["cmd.exe", "/d", "/s", "/c", "echo PORT=%PORT% ARG={port}"]
            : ["/usr/bin/env"];

    private static LocalProcessManifest LongRunningProcess()
        => new()
        {
            Command = OperatingSystem.IsWindows()
                ? ["ping.exe", "-n", "30", "127.0.0.1"]
                : ["/bin/sleep", "30"],
            RestartPolicy = "never"
        };

    private static string DotnetExecutable()
        => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private static async Task<LocalProcessStatus> WaitForStatusAsync(
        LocalProcessManager manager,
        Func<LocalProcessStatus, bool> predicate)
    {
        LocalProcessStatus? result = null;
        await WaitForAsync(() =>
        {
            result = manager.Snapshot().Single();
            return predicate(result);
        });
        return result!;
    }

    private static async Task<string> WaitForLogAsync(string path, string value)
    {
        string? contents = null;
        await WaitForAsync(() =>
        {
            if (!File.Exists(path))
                return false;
            contents = File.ReadAllText(path);
            return contents.Contains(value, StringComparison.Ordinal);
        });
        return contents!;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("Condition was not reached before the timeout.");
    }

    private static int FindAvailableRangeStart()
    {
        for (var port = 46000; port < 60000; port += 20)
        {
            if (Enumerable.Range(port, 13).All(LocalManifestLoader.IsTcpPortAvailable))
                return port;
        }

        throw new InvalidOperationException("No free local port range was found for the test.");
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SlimFaas.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class AsyncDisposableAdapter : IDisposable
    {
        public AsyncDisposableAdapter(LocalProcessManager value)
        {
            Value = value;
        }

        public LocalProcessManager Value { get; }

        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
