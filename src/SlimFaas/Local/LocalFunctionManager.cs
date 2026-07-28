using System.Globalization;
using SlimFaas.Kubernetes;

namespace SlimFaas.Local;

public sealed class LocalFunctionManager : IAsyncDisposable
{
    private readonly LoadedLocalManifest _loaded;
    private readonly LocalPortAllocator _ports;
    private readonly LocalStateStore _state;
    private readonly HttpClient _healthClient = new();
    private readonly Dictionary<string, FunctionRuntime> _functions;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _reconcileTask;
    private long _resourceVersion;

    public LocalFunctionManager(
        LoadedLocalManifest loaded,
        LocalPortAllocator ports,
        LocalStateStore state)
    {
        _loaded = loaded;
        _ports = ports;
        _state = state;
        _functions = loaded.Manifest.Functions.ToDictionary(
            item => item.Key,
            item =>
            {
                FunctionMetadata metadata = FunctionMetadataParser.Parse(item.Value.Annotations, item.Key);
                int maximum = metadata.Scale?.ReplicaMax ??
                              Math.Max(metadata.ReplicasAtStart, metadata.ReplicasMin);
                maximum = Math.Max(maximum, Math.Max(metadata.ReplicasAtStart, metadata.ReplicasMin));
                return new FunctionRuntime(item.Key, item.Value, metadata, maximum)
                {
                    Desired = Math.Min(metadata.ReplicasAtStart, maximum)
                };
            },
            StringComparer.OrdinalIgnoreCase);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _reconcileTask = ReconcileLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

<<<<<<< HEAD
    public IReadOnlySet<string> PortAllocationIds
        => _functions.Values
            .SelectMany(function => Enumerable.Range(0, function.Maximum)
                .Select(index => ReplicaId(function.Name, index)))
            .ToHashSet(StringComparer.Ordinal);

=======
>>>>>>> origin/main
    public Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request, CancellationToken cancellationToken)
    {
        if (!_functions.TryGetValue(request.Deployment, out FunctionRuntime? function))
            return Task.FromResult<ReplicaRequest?>(null);

        lock (function.Gate)
        {
            function.Desired = Math.Clamp(request.Replicas, 0, function.Maximum);
            Interlocked.Increment(ref _resourceVersion);
        }

        return Task.FromResult<ReplicaRequest?>(
            request with { Replicas = Math.Clamp(request.Replicas, 0, function.Maximum) });
    }

    public IList<DeploymentInformation> Snapshot(string namespaceName)
    {
        var result = new List<DeploymentInformation>(_functions.Count);
        foreach (FunctionRuntime function in _functions.Values.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            lock (function.Gate)
            {
                IList<PodInformation> pods = function.Replicas
                    .Where(item => item.Key < function.Desired)
                    .OrderBy(item => item.Key)
                    .Select(item => ToPod(function, item.Value))
                    .ToList();

                result.Add(new DeploymentInformation(
                    Deployment: function.Name,
                    Namespace: namespaceName,
                    Pods: pods,
                    Configuration: function.Metadata.Configuration,
                    Replicas: function.Desired,
                    ReplicasAtStart: function.Metadata.ReplicasAtStart,
                    ReplicasMin: function.Metadata.ReplicasMin,
                    TimeoutSecondBeforeSetReplicasMin:
                    function.Metadata.TimeoutSecondBeforeSetReplicasMin,
                    NumberParallelRequest: function.Metadata.NumberParallelRequest,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest:
                    function.Metadata.ReplicasStartAsSoonAsOneFunctionRetrieveARequest,
                    PodType: PodType.Deployment,
                    DependsOn: function.Metadata.DependsOn,
                    Schedule: function.Metadata.Schedule,
                    SubscribeEvents: function.Metadata.SubscribeEvents,
                    Visibility: function.Metadata.Visibility,
                    PathsStartWithVisibility: function.Metadata.PathsStartWithVisibility,
                    ResourceVersion: Volatile.Read(ref _resourceVersion).ToString(CultureInfo.InvariantCulture),
                    EndpointReady: pods.Any(pod => pod.Ready == true),
                    Trust: function.Metadata.Trust,
                    Scale: function.Metadata.Scale,
                    NumberParallelRequestPerPod: function.Metadata.NumberParallelRequestPerPod));
            }
        }

        return result;
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            do
            {
                foreach (FunctionRuntime function in _functions.Values)
                    await ReconcileAsync(function, cancellationToken);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(FunctionRuntime function, CancellationToken cancellationToken)
    {
        List<ReplicaRuntime> toStop = [];
        List<ReplicaRuntime> toStart = [];
        List<ReplicaRuntime> toProbe = [];
        List<ManagedLocalProcess> exitedProcesses = [];

        lock (function.Gate)
        {
            foreach ((int index, ReplicaRuntime replica) in function.Replicas.ToArray())
            {
                if (index >= function.Desired)
                {
                    function.Replicas.Remove(index);
                    toStop.Add(replica);
                }
            }

            for (var index = 0; index < function.Desired; index++)
            {
                if (!function.Replicas.TryGetValue(index, out ReplicaRuntime? replica))
                {
                    replica = new ReplicaRuntime(index);
                    function.Replicas.Add(index, replica);
                }

                if (replica.Process is { HasExited: true })
                {
                    ManagedLocalProcess exitedProcess = replica.Process;
                    int? exitCode = exitedProcess.ExitCode;
                    exitedProcesses.Add(exitedProcess);
                    replica.Process = null;
                    replica.Ready = false;
                    if (replica.Port.HasValue &&
                        !LocalManifestLoader.IsTcpPortAvailable(replica.Port.Value))
                    {
                        // Another process won the bind race. Do not pin this
                        // replica identity to the now externally occupied port.
                        _ports.RejectAndRetry(ReplicaId(function.Name, replica.Index));
                        replica.Port = null;
                    }
                    replica.AppFailureReason = "ProcessExited";
                    replica.AppFailureMessage = $"Process exited with code {exitCode}.";
                    int failures = ++replica.ConsecutiveFailures;
                    replica.NextStart = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 1 << Math.Min(failures - 1, 5)));
                }

                if (replica.Process is null && DateTimeOffset.UtcNow >= replica.NextStart)
                    toStart.Add(replica);
                else if (replica.Process is { HasExited: false } &&
                         DateTimeOffset.UtcNow >= replica.NextProbe)
                    toProbe.Add(replica);
            }
        }

        foreach (ManagedLocalProcess process in exitedProcesses)
            await process.DisposeAsync();

        foreach (ReplicaRuntime replica in toStop)
        {
            await StopReplicaAsync(function, replica, cancellationToken);
            _ports.Release(ReplicaId(function.Name, replica.Index));
        }

        foreach (ReplicaRuntime replica in toStart)
            StartReplica(function, replica);

        foreach (ReplicaRuntime replica in toProbe)
            await ProbeReplicaAsync(function, replica, cancellationToken);
    }

    private void StartReplica(FunctionRuntime function, ReplicaRuntime replica)
    {
        lock (function.Gate)
        {
            if (replica.Process is not null || replica.Index >= function.Desired)
                return;

            string replicaId = ReplicaId(function.Name, replica.Index);
            int? port = _ports.Reserve(replicaId);
            replica.Port = port;
            if (!port.HasValue)
            {
                replica.StartFailureReason = "PortRangeExhausted";
                replica.StartFailureMessage =
                    $"No free port in {_loaded.Manifest.ProcessPorts.From}-{_loaded.Manifest.ProcessPorts.To}.";
                replica.NextStart = DateTimeOffset.UtcNow.AddSeconds(1);
                return;
            }

            if (!LocalManifestLoader.IsTcpPortAvailable(port.Value))
            {
                _ports.RejectAndRetry(replicaId);
                replica.Port = null;
                replica.NextStart = DateTimeOffset.UtcNow.AddMilliseconds(100);
                return;
            }

            List<string> command = function.Manifest.Command
                .Select(value => LocalManifestLoader.ExpandTemplate(value, port.Value, replica.Index))
                .ToList();
            Dictionary<string, string> environment = function.Manifest.Environment.ToDictionary(
                item => item.Key,
                item => LocalManifestLoader.ExpandTemplate(item.Value, port.Value, replica.Index),
                StringComparer.Ordinal);
            environment["PORT"] = port.Value.ToString(CultureInfo.InvariantCulture);
            environment["SLIMFAAS_PORT"] = port.Value.ToString(CultureInfo.InvariantCulture);
            environment["SLIMFAAS_REPLICA_INDEX"] = replica.Index.ToString(CultureInfo.InvariantCulture);
            if (!environment.ContainsKey("ASPNETCORE_URLS"))
                environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port.Value}";

            try
            {
                string logPath = Path.Combine(_state.LogsDirectory, $"{function.Name}-{replica.Index}.log");
                replica.Process = ManagedLocalProcess.Start(
                    command,
                    Path.GetFullPath(function.Manifest.WorkingDirectory, _loaded.BaseDirectory),
                    environment,
                    $"function/{function.Name}/{replica.Index}",
                    logPath);
                replica.StartedAt = DateTimeOffset.UtcNow;
                replica.StartFailureReason = null;
                replica.StartFailureMessage = null;
                replica.AppFailureReason = null;
                replica.AppFailureMessage = null;
                Interlocked.Increment(ref _resourceVersion);
            }
            catch (Exception exception)
            {
                _ports.RejectAndRetry(replicaId);
                replica.Port = null;
                replica.StartFailureReason = "ProcessStartFailed";
                replica.StartFailureMessage = exception.Message;
                replica.NextStart = DateTimeOffset.UtcNow.AddSeconds(1);
            }
        }
    }

    private async Task ProbeReplicaAsync(
        FunctionRuntime function,
        ReplicaRuntime replica,
        CancellationToken cancellationToken)
    {
        if (!replica.Port.HasValue || replica.Process is null)
            return;

        bool ready = false;
        try
        {
            string healthPath = LocalManifestLoader.ExpandTemplate(
                function.Manifest.Health.Path,
                replica.Port.Value,
                replica.Index);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(function.Manifest.Health.TimeoutSeconds));
            using HttpResponseMessage response = await _healthClient.GetAsync(
                new Uri($"http://127.0.0.1:{replica.Port.Value}{healthPath}"),
                timeout.Token);
            ready = response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException)
        {
            ready = false;
        }

        ManagedLocalProcess? unhealthyProcess = null;
        lock (function.Gate)
        {
            if (replica.Process is null)
                return;
            bool previous = replica.Ready;
            replica.Ready = ready;
            replica.NextProbe = DateTimeOffset.UtcNow.AddSeconds(function.Manifest.Health.PeriodSeconds);
            if (ready)
                replica.ConsecutiveFailures = 0;
            else if (DateTimeOffset.UtcNow - replica.StartedAt >
                     TimeSpan.FromSeconds(function.Manifest.Health.StartupTimeoutSeconds))
            {
                replica.AppFailureReason = "ReadinessTimeout";
                replica.AppFailureMessage =
                    $"Health endpoint did not become ready within " +
                    $"{function.Manifest.Health.StartupTimeoutSeconds} seconds.";
                unhealthyProcess = replica.Process;
                replica.Process = null;
                int failures = ++replica.ConsecutiveFailures;
                replica.NextStart = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min(30, 1 << Math.Min(failures - 1, 5)));
            }

            if (previous != ready)
                Interlocked.Increment(ref _resourceVersion);
        }

        if (unhealthyProcess is not null)
        {
            await unhealthyProcess.StopAsync(null, TimeSpan.FromSeconds(5), cancellationToken);
            await unhealthyProcess.DisposeAsync();
        }
    }

    private async Task StopReplicaAsync(
        FunctionRuntime function,
        ReplicaRuntime replica,
        CancellationToken cancellationToken)
    {
        ManagedLocalProcess? process = replica.Process;
        replica.Process = null;
        replica.Ready = false;
        if (process is null)
            return;

        Uri? shutdownUri = null;
        TimeSpan timeout = TimeSpan.FromSeconds(5);
        if (function.Manifest.Shutdown is not null && replica.Port.HasValue)
        {
            string path = LocalManifestLoader.ExpandTemplate(
                function.Manifest.Shutdown.Path,
                replica.Port.Value,
                replica.Index);
            shutdownUri = new Uri($"http://127.0.0.1:{replica.Port.Value}{path}");
            timeout = TimeSpan.FromSeconds(function.Manifest.Shutdown.TimeoutSeconds);
        }

        await process.StopAsync(shutdownUri, timeout, cancellationToken);
        await process.DisposeAsync();
        Interlocked.Increment(ref _resourceVersion);
    }

    private static PodInformation ToPod(FunctionRuntime function, ReplicaRuntime replica)
    {
        IDictionary<string, string> annotations = function.Manifest.Annotations.ToDictionary(
            item => item.Key,
            item => replica.Port.HasValue
                ? LocalManifestLoader.ExpandTemplate(item.Value, replica.Port.Value, replica.Index)
                : item.Value,
            StringComparer.Ordinal);

        return new PodInformation(
            Name: $"{function.Name}-{replica.Index}",
            Started: replica.Process is { HasExited: false },
            Ready: replica.Ready,
            Ip: "127.0.0.1",
            DeploymentName: function.Name,
            Ports: replica.Port.HasValue ? [replica.Port.Value] : [],
            ResourceVersion: replica.StartedAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ServiceName: function.Name)
        {
            Annotations = annotations,
            StartFailureReason = replica.StartFailureReason,
            StartFailureMessage = replica.StartFailureMessage,
            AppFailureReason = replica.AppFailureReason,
            AppFailureMessage = replica.AppFailureMessage
        };
    }

<<<<<<< HEAD
    internal static string ReplicaId(string function, int index) => $"function:{function}:{index}";
=======
    private static string ReplicaId(string function, int index) => $"function:{function}:{index}";
>>>>>>> origin/main

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_reconcileTask is not null)
            await _reconcileTask;

        foreach (FunctionRuntime function in _functions.Values)
        {
            List<ReplicaRuntime> replicas;
            lock (function.Gate)
            {
                replicas = function.Replicas.Values.ToList();
                function.Replicas.Clear();
            }

            foreach (ReplicaRuntime replica in replicas)
            {
                await StopReplicaAsync(function, replica, CancellationToken.None);
                _ports.Release(ReplicaId(function.Name, replica.Index));
            }
        }

        _healthClient.Dispose();
        _stopping.Dispose();
    }

    private sealed class FunctionRuntime(
        string name,
        LocalFunctionManifest manifest,
        FunctionMetadata metadata,
        int maximum)
    {
        public string Name { get; } = name;
        public LocalFunctionManifest Manifest { get; } = manifest;
        public FunctionMetadata Metadata { get; } = metadata;
        public int Maximum { get; } = maximum;
        public object Gate { get; } = new();
        public SortedDictionary<int, ReplicaRuntime> Replicas { get; } = [];
        public int Desired { get; set; }
    }

    private sealed class ReplicaRuntime(int index)
    {
        public int Index { get; } = index;
        public int? Port { get; set; }
        public ManagedLocalProcess? Process { get; set; }
        public bool Ready { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset NextStart { get; set; }
        public DateTimeOffset NextProbe { get; set; }
        public int ConsecutiveFailures { get; set; }
        public string? StartFailureReason { get; set; }
        public string? StartFailureMessage { get; set; }
        public string? AppFailureReason { get; set; }
        public string? AppFailureMessage { get; set; }
    }
}
