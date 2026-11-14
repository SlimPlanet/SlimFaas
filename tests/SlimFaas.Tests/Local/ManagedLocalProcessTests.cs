using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SlimFaas.Local;

namespace SlimFaas.Tests.Local;

public sealed class ManagedLocalProcessTests
{
    [Fact]
    public void CreateStartInfo_UsesComSpecForWindowsCommandWrappers()
    {
        using var directory = new TemporaryDirectory();
        string wrapper = Path.Combine(directory.Path, "npm.cmd");
        File.WriteAllText(wrapper, "@echo off");

        ProcessStartInfo startInfo = ManagedLocalProcess.CreateStartInfo(
            ["npm", "run", "dev", "--", "--host", "127.0.0.1"],
            directory.Path,
            isWindowsOverride: true,
            commandInterpreterOverride: @"C:\Windows\System32\cmd.exe");

        Assert.Equal(@"C:\Windows\System32\cmd.exe", startInfo.FileName);
        Assert.StartsWith("/d /s /c ", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains(wrapper, startInfo.Arguments);
        Assert.Contains("\"run\"", startInfo.Arguments);
        Assert.Contains("\"--host\"", startInfo.Arguments);
        Assert.Contains("\"127.0.0.1\"", startInfo.Arguments);
        Assert.Empty(startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateStartInfo_DoesNotTransformUnixCommandsIntoShellStrings()
    {
        using var directory = new TemporaryDirectory();

        ProcessStartInfo startInfo = ManagedLocalProcess.CreateStartInfo(
            ["npm", "run", "dev"],
            directory.Path,
            isWindowsOverride: false);

        Assert.Equal("npm", startInfo.FileName);
        Assert.Equal(["run", "dev"], startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public async Task Start_RunsAWindowsCommandWrapperFromAPathWithSpaces()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var directory = new TemporaryDirectory();
        string wrapper = Path.Combine(directory.Path, "wrapper with spaces.cmd");
        string logPath = Path.Combine(directory.Path, "wrapper.log");
        File.WriteAllText(
            wrapper,
            "@echo off\r\n" +
            "echo VALUE=%WRAPPER_TEST_VALUE%\r\n" +
            "echo ARG=%~1\r\n");

        int? exitCode;
        await using (ManagedLocalProcess process = ManagedLocalProcess.Start(
                         ["wrapper with spaces", "argument with spaces"],
                         directory.Path,
                         new Dictionary<string, string> { ["WRAPPER_TEST_VALUE"] = "windows" },
                         "process/wrapper-test",
                         logPath))
        {
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }

        string log = File.ReadAllText(logPath);
        Assert.Equal(0, exitCode);
        Assert.Contains("VALUE=windows", log, StringComparison.Ordinal);
        Assert.Contains("ARG=argument with spaces", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_TerminatesTheWholeChildProcessTree()
    {
        await using var directory = new TemporaryDirectory();
        string childPidPath = Path.Combine(directory.Path, "child.pid");
        List<string> command;
        if (OperatingSystem.IsWindows())
        {
            string escapedPath = childPidPath.Replace("'", "''", StringComparison.Ordinal);
            command =
            [
                "powershell.exe",
                "-NoProfile",
                "-Command",
                "$child = Start-Process ping.exe -ArgumentList @('-t','127.0.0.1') -WindowStyle Hidden -PassThru; " +
                $"Set-Content -LiteralPath '{escapedPath}' -Value $child.Id; " +
                "Wait-Process -Id $child.Id"
            ];
        }
        else
        {
            string escapedPath = childPidPath.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            command =
            [
                "/bin/sh",
                "-c",
                $"sleep 30 & child=$!; echo $child > '{escapedPath}'; wait $child"
            ];
        }

        await using ManagedLocalProcess process = ManagedLocalProcess.Start(
            command,
            directory.Path,
            new Dictionary<string, string>(),
            "process/tree-test",
            Path.Combine(directory.Path, "tree.log"));
        int childPid = await WaitForChildPidAsync(childPidPath);
        Assert.True(IsRunning(childPid));
        if (OperatingSystem.IsWindows())
        {
            DateTime startedAt;
            using (Process root = Process.GetProcessById(process.Id))
                startedAt = root.StartTime;
            using ProcessTreeSnapshot snapshot = WindowsProcessTree.CaptureDescendants(process.Id, startedAt);
            Assert.Contains(snapshot.GetLiveDescendants(), descendant => descendant.ProcessId == childPid);
            Assert.DoesNotContain(snapshot.GetLiveDescendants(), descendant => descendant.ProcessId == Environment.ProcessId);
        }

        await process.StopAsync(null, TimeSpan.FromSeconds(5));

        await WaitForAsync(() => !IsRunning(childPid), ShutdownDeadline);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task CapturedTree_PreservesTheFailureForAnOrphanedGrandchild()
    {
        if (!OperatingSystem.IsWindows())
            return;

        await using var directory = new TemporaryDirectory();
        string childPidPath = Path.Combine(directory.Path, "intermediate.pid");
        string grandchildPidPath = Path.Combine(directory.Path, "grandchild.pid");
        string childScript =
            "$child = Start-Process ping.exe -ArgumentList @('-t','127.0.0.1') -WindowStyle Hidden -PassThru; " +
            $"Set-Content -LiteralPath '{grandchildPidPath.Replace("'", "''", StringComparison.Ordinal)}' -Value $child.Id; " +
            "Wait-Process -Id $child.Id";
        string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
        await using ManagedLocalProcess root = ManagedLocalProcess.Start(
            ["powershell.exe", "-NoProfile", "-Command",
                "$child = Start-Process powershell.exe -WindowStyle Hidden -PassThru " +
                $"-ArgumentList @('-NoProfile','-EncodedCommand','{encodedScript}'); " +
                $"Set-Content -LiteralPath '{childPidPath.Replace("'", "''", StringComparison.Ordinal)}' -Value $child.Id; " +
                "Wait-Process -Id $child.Id"],
            directory.Path,
            new Dictionary<string, string>(),
            "process/orphan-test",
            Path.Combine(directory.Path, "tree.log"));
        using Process child = Process.GetProcessById(await WaitForChildPidAsync(childPidPath));
        using Process grandchild = Process.GetProcessById(await WaitForChildPidAsync(grandchildPidPath));
        try
        {
            using Process parent = Process.GetProcessById(root.Id);
            using ProcessTreeSnapshot snapshot = WindowsProcessTree.CaptureDescendants(root.Id, parent.StartTime);
            Assert.Contains(snapshot.GetLiveDescendants(), entry => entry.ProcessId == grandchild.Id);

            parent.Kill();
            child.Kill();
            await Task.WhenAll(parent.WaitForExitAsync(), child.WaitForExitAsync()).WaitAsync(ShutdownDeadline);
            await WaitForAsync(() => !snapshot.GetLiveDescendants().Any(entry => entry.ProcessId == child.Id),
                ShutdownDeadline);

            // Both intermediate ancestors are absent, but the retained handle still identifies the grandchild.
            Assert.DoesNotContain(snapshot.GetLiveDescendants(), entry => entry.ProcessId == child.Id);
            Assert.Contains(snapshot.GetLiveDescendants(), entry => entry.ProcessId == grandchild.Id);
            var failure = new AggregateException(new Win32Exception(AccessDenied));
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ManagedLocalProcess.WaitForTreeExitAsync(failure, root.Id, () => root.HasExited,
                    snapshot.GetLiveDescendants, TimeSpan.Zero, CancellationToken.None));
            Assert.Same(failure, exception.InnerException);
            Assert.Contains(grandchild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                exception.Message, StringComparison.Ordinal);

            grandchild.Kill();
            await grandchild.WaitForExitAsync().WaitAsync(ShutdownDeadline);
            await ManagedLocalProcess.WaitForTreeExitAsync(failure, root.Id, () => root.HasExited,
                snapshot.GetLiveDescendants, ShutdownDeadline, CancellationToken.None);
        }
        finally
        {
            foreach (Process process in new[] { child, grandchild })
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(ShutdownDeadline);
            }
        }
    }

    [Fact]
    public async Task WaitForTreeExitAsync_WaitsForAnAlreadyTerminatingProcess()
    {
        var failure = new AggregateException(new Win32Exception(AccessDenied));
        int polls = 0;

        await ManagedLocalProcess.WaitForTreeExitAsync(
            failure,
            processId: 42,
            hasExited: () => ++polls >= 3,
            liveDescendants: () => [],
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task WaitForTreeExitAsync_WaitsForDescendantsAfterTheParentExited()
    {
        var failure = new AggregateException(new Win32Exception(AccessDenied));
        int polls = 0;

        await ManagedLocalProcess.WaitForTreeExitAsync(
            failure,
            processId: 42,
            hasExited: () => true,
            liveDescendants: () => ++polls >= 3 ? [] : new[] { new ProcessTreeEntry(43, 42) },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task WaitForTreeExitAsync_PreservesTheFailureWhenADescendantOutlivesTheParent()
    {
        var failure = new AggregateException(new Win32Exception(AccessDenied));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagedLocalProcess.WaitForTreeExitAsync(
                failure,
                processId: 42,
                hasExited: () => true,
                liveDescendants: () => [new ProcessTreeEntry(43, 42), new ProcessTreeEntry(44, 43)],
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None));

        Assert.Same(failure, exception.InnerException);
        Assert.Contains("42", exception.Message, StringComparison.Ordinal);
        Assert.Contains("43, 44", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForTreeExitAsync_PreservesTheFailureWhenTheParentDoesNotExit()
    {
        var failure = new Win32Exception(AccessDenied);
        var descendantsQueried = false;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ManagedLocalProcess.WaitForTreeExitAsync(
                failure,
                processId: 42,
                hasExited: () => false,
                liveDescendants: () =>
                {
                    descendantsQueried = true;
                    return [];
                },
                TimeSpan.FromMilliseconds(200),
                CancellationToken.None));

        Assert.Same(failure, exception.InnerException);
        Assert.False(descendantsQueried);
    }

    [Fact]
    public async Task WaitForTreeExitAsync_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new Win32Exception(AccessDenied);

        Task waiting = ManagedLocalProcess.WaitForTreeExitAsync(
            failure,
            processId: 42,
            hasExited: () => false,
            liveDescendants: () => [],
            TimeSpan.FromSeconds(30),
            cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    [Fact]
    public void SelectDescendants_ReturnsTheSubtreeThatStartedAfterItsParents()
    {
        DateTime rootStartedAt = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        var startTimes = new Dictionary<int, DateTime?>
        {
            [2] = rootStartedAt.AddSeconds(1),
            [3] = rootStartedAt.AddSeconds(2),
            // Started before the root: its recorded parent identifier was reused by the root.
            [4] = rootStartedAt.AddSeconds(-1),
            [5] = rootStartedAt.AddSeconds(3),
            // Unreadable start time: cannot be established as a descendant.
            [6] = null,
            [7] = rootStartedAt.AddSeconds(1)
        };
        ProcessTreeEntry[] processes =
        [
            new(1, 0),
            new(2, 1),
            new(3, 2),
            new(4, 1),
            new(5, 4),
            new(6, 1),
            new(7, 99)
        ];

        List<ProcessTreeEntry> descendants = ProcessTree.SelectDescendants(
            1,
            rootStartedAt,
            processes,
            processId => startTimes[processId]);

        Assert.Equal([new ProcessTreeEntry(2, 1), new ProcessTreeEntry(3, 2)], descendants);
    }

    [Fact]
    public void SelectDescendants_TerminatesOnParentIdentifierCycles()
    {
        DateTime rootStartedAt = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        ProcessTreeEntry[] processes = [new(1, 2), new(2, 1), new(3, 2)];

        List<ProcessTreeEntry> descendants = ProcessTree.SelectDescendants(
            1,
            rootStartedAt,
            processes,
            _ => rootStartedAt.AddSeconds(1));

        Assert.Equal([new ProcessTreeEntry(2, 1), new ProcessTreeEntry(3, 2)], descendants);
    }

    private const int AccessDenied = 5;

    private static async Task<int> WaitForChildPidAsync(string path)
    {
        int childPid = 0;
        await WaitForAsync(() =>
        {
            if (!File.Exists(path))
                return false;
            try
            {
                return int.TryParse(File.ReadAllText(path).Trim(), out childPid);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                // PowerShell's Set-Content can still hold the newly created file open.
                // Wait for a readable PID within the existing bounded startup deadline.
                return false;
            }
        }, StartupDeadline);
        return childPid;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // Spawning PowerShell on a shared Windows runner (with coverage instrumentation in the
    // SonarCloud job) can take several seconds, so the startup deadline is generous. A
    // passing run is not slower: the condition is polled every 100 ms (issue #359).
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(30);

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        long startedAt = Stopwatch.GetTimestamp();
        while (true)
        {
            if (condition())
                return;
            if (Stopwatch.GetElapsedTime(startedAt) >= timeout)
                break;
            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException(
            $"Condition was not reached within {timeout.TotalSeconds:F0} s.");
    }

    private sealed class TemporaryDirectory : IDisposable, IAsyncDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "SlimFaas.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }

        public async ValueTask DisposeAsync()
        {
            // Windows can retain the process working-directory handle briefly after exit.
            // Keep process-exit assertions separate; only retry the final directory removal.
            await WaitForAsync(() =>
            {
                try
                {
                    Dispose();
                    return true;
                }
                catch (IOException) when (OperatingSystem.IsWindows())
                {
                    return false;
                }
            }, ShutdownDeadline);
        }
    }
}
