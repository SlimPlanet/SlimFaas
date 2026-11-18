using SlimFaas.Kubernetes;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class LocalNodeManagerTests
{
    [Fact]
    public void NodeCommand_LeavesOtherPlatformsUnchanged()
    {
        string[] command = ["/runtime/SlimFaas", "argument with spaces", "$(touch forbidden)"];
        Assert.Same(command, LocalNodeManager.BuildNodeCommand(command, isMacOS: false));
    }

    [Fact]
    public void NodeCommand_OnMacPassesArgumentsOutsideTheShellScript()
    {
        string[] command = ["/runtime with spaces/SlimFaas", "$(touch forbidden)"];
        IReadOnlyList<string> wrapped = LocalNodeManager.BuildNodeCommand(command, isMacOS: true);
        Assert.Equal("/bin/sh", wrapped[0]);
        Assert.Equal("-c", wrapped[1]);
        Assert.Contains("\"$@\"", wrapped[2], StringComparison.Ordinal);
        Assert.DoesNotContain(command[1], wrapped[2], StringComparison.Ordinal);
        Assert.Equal(command, wrapped.Skip(4));
    }

    [Fact]
    public async Task NodeCommand_SystemLauncherPreservesExitStatusAndLiteralArguments()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        DirectoryInfo directory = Directory.CreateTempSubdirectory("slimfaas-node-launcher-");
        string logPath = Path.Combine(directory.FullName, "launcher.log");
        IReadOnlyList<string> command = LocalNodeManager.BuildNodeCommand(
            ["/bin/sh", "-c", "printf '%s\\n' \"$1\"; exit 37", "test-node", "$(touch forbidden)"],
            isMacOS: true);
        try
        {
            int? exitCode;
            await using (ManagedLocalProcess process = ManagedLocalProcess.Start(
                             command, directory.FullName, new Dictionary<string, string>(), "node/test", logPath))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
                exitCode = process.ExitCode;
            }
            Assert.Equal(37, exitCode);
            Assert.Contains("$(touch forbidden)", File.ReadAllText(logPath), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory.FullName, "forbidden")));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

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
            Assert.Equal(
                Environment.GetEnvironmentVariable("SlimFaas__Streams__EnableFront") ?? "true",
                environment["SlimFaas__Streams__EnableFront"]);
            Assert.Equal(
                Environment.GetEnvironmentVariable("SlimFaas__Streams__DefaultVisibility") ?? "Public",
                environment["SlimFaas__Streams__DefaultVisibility"]);
            Assert.Equal(
                Environment.GetEnvironmentVariable("SlimFaas__DeadLetterQueue__EnableFront") ?? "true",
                environment["SlimFaas__DeadLetterQueue__EnableFront"]);
            Assert.Equal(
                Environment.GetEnvironmentVariable("SlimFaas__DeadLetterQueue__DefaultVisibility") ?? "Public",
                environment["SlimFaas__DeadLetterQueue__DefaultVisibility"]);
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
