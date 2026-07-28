using System.Diagnostics;
using System.Text;

namespace SlimFaas.Local;

public sealed class ManagedLocalProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly object _outputGate = new();

    private ManagedLocalProcess(Process process, StreamWriter log)
    {
        _process = process;
        _log = log;
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

        var startInfo = new ProcessStartInfo
        {
            FileName = command[0],
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        for (var index = 1; index < command.Count; index++)
            startInfo.ArgumentList.Add(command[index]);
        foreach ((string name, string value) in environment)
            startInfo.Environment[name] = value;

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        var managed = new ManagedLocalProcess(process, log);
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
