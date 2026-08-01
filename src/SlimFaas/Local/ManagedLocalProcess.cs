using System.Diagnostics;
using System.Text;

namespace SlimFaas.Local;

public sealed class ManagedLocalProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly string _tag;
    private readonly object _outputGate = new();

    private ManagedLocalProcess(Process process, StreamWriter log, string tag)
    {
        _process = process;
        _log = log;
        _tag = tag;
    }

    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public int Id => _process.Id;

    public static ManagedLocalProcess Start(
        IReadOnlyList<string> command,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string tag,
        string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new StreamWriter(
            new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };

        ProcessStartInfo startInfo = CreateStartInfo(command, workingDirectory);
        foreach ((string name, string value) in environment)
            startInfo.Environment[name] = value;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var managed = new ManagedLocalProcess(process, log, tag);
        process.OutputDataReceived += (_, eventArgs) => managed.WriteLine(tag, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => managed.WriteLine(tag, eventArgs.Data);

        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Unable to start '{command[0]}'.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            managed.WriteLine(tag, $"started pid={process.Id}");
            return managed;
        }
        catch
        {
            process.Dispose();
            log.Dispose();
            throw;
        }
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default)
        => _process.WaitForExitAsync(cancellationToken);

    public void WriteStatus(string value) => WriteLine(_tag, value);

    public static void WriteStatus(string tag, string logPath, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        string line = $"[{tag}] {value}";
        Console.WriteLine(line);
        File.AppendAllText(
            logPath,
            $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> command,
        string workingDirectory,
        bool? isWindowsOverride = null,
        string? commandInterpreterOverride = null)
    {
        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        string executable = command[0];
        IReadOnlyList<string> arguments = command.Skip(1).ToArray();
        string? windowsExecutable = isWindows
            ? ResolveWindowsExecutable(executable, workingDirectory)
            : null;
        string? windowsExtension = windowsExecutable is null
            ? null
            : Path.GetExtension(windowsExecutable);
        bool isBatch = windowsExtension is not null &&
                       (windowsExtension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        windowsExtension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

        var startInfo = new ProcessStartInfo
        {
            FileName = isBatch
                ? commandInterpreterOverride ??
                  Environment.GetEnvironmentVariable("ComSpec") ??
                  "cmd.exe"
                : windowsExecutable ?? executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (isBatch)
        {
            // ArgumentList applies the C runtime's backslash escaping. cmd.exe does not
            // use that escaping and receives literal \" characters when the batch path
            // contains spaces. Supply its command line verbatim instead.
            startInfo.Arguments = $"/d /s /c {BuildWindowsBatchCommand(windowsExecutable!, arguments)}";
        }
        else
        {
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static string BuildWindowsBatchCommand(
        string executable,
        IReadOnlyList<string> arguments)
    {
        static string Quote(string value)
            => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        return $"\"{string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote))}\"";
    }

    private static string? ResolveWindowsExecutable(string executable, string workingDirectory)
    {
        bool containsDirectory = executable.Contains(Path.DirectorySeparatorChar) ||
                                 executable.Contains(Path.AltDirectorySeparatorChar);
        IEnumerable<string> directories = containsDirectory
            ? [workingDirectory]
            : new[] { workingDirectory }
                .Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        string[] extensions = string.IsNullOrEmpty(Path.GetExtension(executable))
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        foreach (string directory in directories)
        {
            string candidateBase = Path.IsPathFullyQualified(executable)
                ? executable
                : Path.GetFullPath(executable, directory);
            foreach (string extension in extensions)
            {
                string candidate = candidateBase + extension.ToLowerInvariant();
                if (File.Exists(candidate))
                    return candidate;
                candidate = candidateBase + extension.ToUpperInvariant();
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public async Task StopAsync(
        Uri? shutdownUri,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_process.HasExited)
            return;

        if (shutdownUri is not null)
        {
            try
            {
                using var client = new HttpClient { Timeout = timeout };
                using HttpResponseMessage _ = await client.PostAsync(shutdownUri, null, cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                // The shutdown hook is advisory. The process tree is terminated below.
            }

            if (!_process.HasExited)
            {
                try
                {
                    using var gracefulTimeout =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gracefulTimeout.CancelAfter(timeout);
                    await _process.WaitForExitAsync(gracefulTimeout.Token);
                }
                catch (OperationCanceledException)
                {
                    // Fall through to process-tree termination.
                }
            }
        }

        if (_process.HasExited)
            return;

        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the checks.
        }
    }

    private void WriteLine(string tag, string? value)
    {
        if (value is null)
            return;
        string line = $"[{tag}] {value}";
        lock (_outputGate)
        {
            Console.WriteLine(line);
            _log.WriteLine($"{DateTimeOffset.UtcNow:O} {line}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            await StopAsync(null, TimeSpan.Zero);
        _process.Dispose();
        lock (_outputGate)
            _log.Dispose();
    }
}
