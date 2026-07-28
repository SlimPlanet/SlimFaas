using System.Diagnostics;
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
        Assert.Equal("/d", startInfo.ArgumentList[0]);
        Assert.Equal("/s", startInfo.ArgumentList[1]);
        Assert.Equal("/c", startInfo.ArgumentList[2]);
        Assert.Contains(wrapper, startInfo.ArgumentList[3]);
        Assert.Contains("\"run\"", startInfo.ArgumentList[3]);
        Assert.Contains("\"--host\"", startInfo.ArgumentList[3]);
        Assert.Contains("\"127.0.0.1\"", startInfo.ArgumentList[3]);
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
    public async Task StopAsync_TerminatesTheWholeChildProcessTree()
    {
        using var directory = new TemporaryDirectory();
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
                "$child = Start-Process ping.exe -ArgumentList @('-t','127.0.0.1') -PassThru; " +
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

        await process.StopAsync(null, TimeSpan.FromSeconds(5));

        await WaitForAsync(() => !IsRunning(childPid));
        Assert.True(process.HasExited);
    }

    private static async Task<int> WaitForChildPidAsync(string path)
    {
        int childPid = 0;
        await WaitForAsync(() =>
        {
            if (!File.Exists(path))
                return false;
            return int.TryParse(File.ReadAllText(path).Trim(), out childPid);
        });
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

    private sealed class TemporaryDirectory : IDisposable
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
    }
}
