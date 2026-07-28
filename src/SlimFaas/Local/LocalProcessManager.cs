using System.Globalization;

namespace SlimFaas.Local;

internal enum LocalProcessRestartPolicy
{
    Always,
    OnFailure,
    Never
}

internal static class LocalProcessRestartPolicyParser
{
    public static bool TryParse(string value, out LocalProcessRestartPolicy policy)
    {
        if (value.Equals("always", StringComparison.OrdinalIgnoreCase))
        {
            policy = LocalProcessRestartPolicy.Always;
            return true;
        }

        if (value.Equals("onFailure", StringComparison.OrdinalIgnoreCase))
        {
            policy = LocalProcessRestartPolicy.OnFailure;
            return true;
        }

        if (value.Equals("never", StringComparison.OrdinalIgnoreCase))
        {
            policy = LocalProcessRestartPolicy.Never;
            return true;
        }

        policy = default;
        return false;
    }
}

public sealed class LocalProcessManager : IAsyncDisposable
{
    private static readonly TimeSpan StableRunDuration = TimeSpan.FromSeconds(30);

    private readonly LoadedLocalManifest _loaded;
    private readonly LocalPortAllocator _ports;
    private readonly LocalStateStore _state;
    private readonly Dictionary<string, ProcessRuntime> _processes;
    private readonly CancellationTokenSource _stopping = new();
    private CancellationTokenSource? _linkedStopping;
    private Task? _monitorTask;

    public LocalProcessManager(
        LoadedLocalManifest loaded,
        LocalPortAllocator ports,
        LocalStateStore state)
    {
        _loaded = loaded;
        _ports = ports;
        _state = state;
        _processes = loaded.Manifest.Processes.ToDictionary(
            item => item.Key,
            item => new ProcessRuntime(item.Key, item.Value),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> PortAllocationIds
        => _processes.Values
            .Where(process => process.PortMode != LocalProcessPortMode.None)
            .Select(process => AllocationId(process.Name))
            .ToHashSet(StringComparer.Ordinal);

    public void PreparePorts()
    {
        foreach (ProcessRuntime runtime in _processes.Values
                     .Where(process => process.PortMode == LocalProcessPortMode.Fixed))
        {
            runtime.Port = _ports.ReserveFixed(AllocationId(runtime.Name), runtime.FixedPort!.Value);
            if (!runtime.Port.HasValue)
            {
                throw new LocalManifestException(
                    $"processes.{runtime.Name}.port {runtime.FixedPort.Value} is already in use.");
            }
        }

        foreach (ProcessRuntime runtime in _processes.Values
                     .Where(process => process.PortMode == LocalProcessPortMode.Auto))
        {
            runtime.Port = _ports.Reserve(AllocationId(runtime.Name));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _linkedStopping = CancellationTokenSource.CreateLinkedTokenSource(
            _stopping.Token,
            cancellationToken);
        _monitorTask = MonitorLoopAsync(_linkedStopping.Token);
        return Task.CompletedTask;
    }

    internal IReadOnlyList<LocalProcessStatus> Snapshot()
        => _processes.Values
            .Select(process => new LocalProcessStatus(
                process.Name,
                process.Process is { HasExited: false },
                process.Terminal,
                process.Port,
                process.AttemptCount,
                process.StartCount,
                process.LastExitCode))
            .ToList();

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ProcessRuntime runtime in _processes.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ReconcileAsync(runtime, cancellationToken);
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(
        ProcessRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Process is { HasExited: true } exitedProcess)
        {
            int exitCode = exitedProcess.ExitCode ?? -1;
            runtime.LastExitCode = exitCode;
            runtime.Process = null;
            exitedProcess.WriteStatus($"exited code={exitCode}");
            await exitedProcess.DisposeAsync();

            if (DateTimeOffset.UtcNow - runtime.StartedAt >= StableRunDuration)
                runtime.ConsecutiveFailures = 0;

            bool restart = runtime.RestartPolicy switch
            {
                LocalProcessRestartPolicy.Always => true,
                LocalProcessRestartPolicy.OnFailure => exitCode != 0,
                _ => false
            };
            if (restart)
            {
                ScheduleRetry(runtime);
            }
            else
            {
                MarkTerminal(runtime, $"will not restart after exit code {exitCode}");
            }
        }

        if (runtime.Process is null &&
            !runtime.Terminal &&
            DateTimeOffset.UtcNow >= runtime.NextStart)
        {
            Start(runtime);
        }
    }

    private void Start(ProcessRuntime runtime)
    {
        if (!EnsurePort(runtime))
            return;

        int? port = runtime.Port;
        List<string> command = runtime.Manifest.Command
            .Select(value => port.HasValue ? ExpandPort(value, port.Value) : value)
            .ToList();
        Dictionary<string, string> environment = runtime.Manifest.Environment.ToDictionary(
            item => item.Key,
            item => port.HasValue ? ExpandPort(item.Value, port.Value) : item.Value,
            StringComparer.Ordinal);
        if (port.HasValue)
            environment["PORT"] = port.Value.ToString(CultureInfo.InvariantCulture);

        try
        {
            runtime.AttemptCount++;
            runtime.Process = ManagedLocalProcess.Start(
                command,
                Path.GetFullPath(runtime.Manifest.WorkingDirectory, _loaded.BaseDirectory),
                environment,
                Tag(runtime.Name),
                LogPath(runtime.Name));
            runtime.StartedAt = DateTimeOffset.UtcNow;
            runtime.StartCount++;
            runtime.LastExitCode = null;
        }
        catch (Exception exception)
        {
            ManagedLocalProcess.WriteStatus(
                Tag(runtime.Name),
                LogPath(runtime.Name),
                $"start failed: {exception.Message}");
            HandleStartFailure(runtime);
        }
    }

    private bool EnsurePort(ProcessRuntime runtime)
    {
        if (runtime.PortMode == LocalProcessPortMode.None)
            return true;

        if (!runtime.Port.HasValue)
        {
            runtime.Port = runtime.PortMode == LocalProcessPortMode.Fixed
                ? _ports.ReserveFixed(AllocationId(runtime.Name), runtime.FixedPort!.Value)
                : _ports.Reserve(AllocationId(runtime.Name));
        }

        if (runtime.Port.HasValue && LocalManifestLoader.IsTcpPortAvailable(runtime.Port.Value))
            return true;

        if (runtime.PortMode == LocalProcessPortMode.Auto)
        {
            _ports.RejectAndRetry(AllocationId(runtime.Name));
            runtime.Port = null;
        }

        ManagedLocalProcess.WriteStatus(
            Tag(runtime.Name),
            LogPath(runtime.Name),
            runtime.PortMode == LocalProcessPortMode.Auto
                ? $"start failed: no free port in {_loaded.Manifest.ProcessPorts.From}-" +
                  $"{_loaded.Manifest.ProcessPorts.To}"
                : $"start failed: port {runtime.FixedPort} is already in use");
        HandleStartFailure(runtime);
        return false;
    }

    private void HandleStartFailure(ProcessRuntime runtime)
    {
        runtime.LastExitCode = -1;
        if (runtime.RestartPolicy == LocalProcessRestartPolicy.Never)
            MarkTerminal(runtime, "will not retry after start failure");
        else
            ScheduleRetry(runtime);
    }

    private void ScheduleRetry(ProcessRuntime runtime)
    {
        int failures = ++runtime.ConsecutiveFailures;
        runtime.NextStart = DateTimeOffset.UtcNow + RetryDelay(failures);
    }

    internal static TimeSpan RetryDelay(int consecutiveFailures)
        => TimeSpan.FromSeconds(
            Math.Min(30, 1 << Math.Min(Math.Max(0, consecutiveFailures - 1), 5)));

    private void MarkTerminal(ProcessRuntime runtime, string message)
    {
        runtime.Terminal = true;
        ManagedLocalProcess.WriteStatus(Tag(runtime.Name), LogPath(runtime.Name), message);
        if (runtime.PortMode != LocalProcessPortMode.None)
            _ports.Release(AllocationId(runtime.Name));
    }

    private static string ExpandPort(string value, int port)
        => value.Replace(
            "{port}",
            port.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private string LogPath(string name) => Path.Combine(_state.LogsDirectory, $"process-{name}.log");
    private static string Tag(string name) => $"process/{name}";
    private static string AllocationId(string name) => $"process:{name}";

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_monitorTask is not null)
            await _monitorTask;

        foreach (ProcessRuntime runtime in _processes.Values)
        {
            ManagedLocalProcess? process = runtime.Process;
            runtime.Process = null;
            runtime.Terminal = true;
            if (process is not null)
            {
                await process.StopAsync(null, TimeSpan.FromSeconds(5));
                await process.DisposeAsync();
            }

            if (runtime.PortMode != LocalProcessPortMode.None)
                _ports.Release(AllocationId(runtime.Name));
        }

        _linkedStopping?.Dispose();
        _stopping.Dispose();
    }

    private sealed class ProcessRuntime
    {
        public ProcessRuntime(string name, LocalProcessManifest manifest)
        {
            Name = name;
            Manifest = manifest;
            RestartPolicy = LocalProcessRestartPolicyParser.TryParse(
                manifest.RestartPolicy,
                out LocalProcessRestartPolicy policy)
                ? policy
                : throw new LocalManifestException(
                    $"processes.{name}.restartPolicy '{manifest.RestartPolicy}' is invalid.");

            if (manifest.Port is null)
            {
                PortMode = LocalProcessPortMode.None;
            }
            else if (manifest.Port.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                PortMode = LocalProcessPortMode.Auto;
            }
            else
            {
                PortMode = LocalProcessPortMode.Fixed;
                FixedPort = int.Parse(manifest.Port, NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        public string Name { get; }
        public LocalProcessManifest Manifest { get; }
        public LocalProcessRestartPolicy RestartPolicy { get; }
        public LocalProcessPortMode PortMode { get; }
        public int? FixedPort { get; }
        public ManagedLocalProcess? Process { get; set; }
        public int? Port { get; set; }
        public bool Terminal { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int AttemptCount { get; set; }
        public int StartCount { get; set; }
        public int? LastExitCode { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset NextStart { get; set; }
    }

    private enum LocalProcessPortMode
    {
        None,
        Auto,
        Fixed
    }
}

internal sealed record LocalProcessStatus(
    string Name,
    bool Running,
    bool Terminal,
    int? Port,
    int AttemptCount,
    int StartCount,
    int? LastExitCode);
