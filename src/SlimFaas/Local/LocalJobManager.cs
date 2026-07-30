using System.Collections.Concurrent;
using SlimFaas.Kubernetes;

namespace SlimFaas.Local;

public sealed class LocalJobManager : IAsyncDisposable
{
    private static readonly CreateJobResources CompatibilityResources = new(
        new Dictionary<string, string> { ["cpu"] = "100m", ["memory"] = "100Mi" },
        new Dictionary<string, string> { ["cpu"] = "100m", ["memory"] = "100Mi" });

    private readonly LoadedLocalManifest _loaded;
    private readonly LocalStateStore _state;
    private readonly string _token;
    private readonly ConcurrentDictionary<string, JobRuntime> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _knownJobNames = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _monitorTask;

    public LocalJobManager(
        LoadedLocalManifest loaded,
        LocalStateStore state,
        string token)
    {
        _loaded = loaded;
        _state = state;
        _token = token;
        Configuration = BuildConfiguration(loaded.Manifest.Jobs);
    }

    public SlimFaasJobConfiguration Configuration { get; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitorTask = MonitorLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public Task CreateAsync(ProcessCreateJobCommand command, CancellationToken cancellationToken)
    {
        if (!_loaded.Manifest.Jobs.TryGetValue(command.Name, out LocalJobManifest? manifest))
            throw new LocalManifestException($"Job configuration '{command.Name}' does not exist.");

        // jobFullName is generated once in the distributed queue. TryAdd makes a
        // leader hand-off safe: a second node cannot start the same process.
        var runtime = new JobRuntime(command, manifest);
        if (!_knownJobNames.TryAdd(command.JobFullName, 0) ||
            !_jobs.TryAdd(command.JobFullName, runtime))
            return Task.CompletedTask;

        Start(runtime);
        return Task.CompletedTask;
    }

    public IList<Job> Snapshot()
        => _jobs.Values
            .OrderByDescending(runtime => runtime.Command.InQueueTimestamp)
            .Select(runtime =>
            {
                lock (runtime.Gate)
                {
                    List<string> dependsOn = runtime.Command.CreateJob.DependsOn ??
                                             Configuration.Configurations[runtime.Command.Name].DependsOn ??
                                             [];
                    return new Job(
                        Name: runtime.Command.JobFullName,
                        Status: runtime.Status,
                        Ips: [],
                        DependsOn: dependsOn,
                        ElementId: runtime.Command.ElementId,
                        InQueueTimestamp: runtime.Command.InQueueTimestamp,
                        StartTimestamp: runtime.StartTimestamp);
                }
            })
            .ToList();

    public async Task DeleteAsync(string fullName, CancellationToken cancellationToken)
    {
        if (!_jobs.TryRemove(fullName, out JobRuntime? runtime))
            return;

        ManagedLocalProcess? process;
        LocalJobGateway? gateway;
        lock (runtime.Gate)
        {
            process = runtime.Process;
            runtime.Process = null;
            gateway = runtime.Gateway;
            runtime.Gateway = null;
        }

        if (process is not null)
        {
            await process.StopAsync(null, TimeSpan.FromSeconds(5), cancellationToken);
            await process.DisposeAsync();
        }
        if (gateway is not null)
            await gateway.DisposeAsync();
    }

    private void Start(JobRuntime runtime)
    {
        lock (runtime.Gate)
        {
            if (runtime.Process is not null || runtime.Completed)
                return;

            runtime.Gateway ??= new LocalJobGateway(
                _loaded.Manifest.Cluster.EntrypointPort,
                runtime.Command.JobFullName,
                _token);
            runtime.Gateway.Start();
            int gatewayPort = runtime.Gateway.Port;

            List<string> command = [.. runtime.Manifest.Command, .. runtime.Command.CreateJob.Args];
            command = command
                .Select(value => RewriteEntrypointUrls(
                    value,
                    _loaded.Manifest.Cluster.EntrypointPort,
                    gatewayPort))
                .ToList();
            Dictionary<string, string> environment =
                runtime.Manifest.Environment.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            foreach (EnvVarInput item in runtime.Command.CreateJob.Environments ?? [])
            {
                if (item.SecretRef is null && item.ConfigMapRef is null &&
                    item.FieldRef is null && item.ResourceFieldRef is null)
                {
                    environment[item.Name] = item.Value;
                }
            }
            foreach (string name in environment.Keys.ToArray())
            {
                environment[name] = RewriteEntrypointUrls(
                    environment[name],
                    _loaded.Manifest.Cluster.EntrypointPort,
                    gatewayPort);
            }
            environment["SLIMFAAS_JOB_NAME"] = runtime.Command.Name;
            environment["SLIMFAAS_JOB_RUN_NAME"] = runtime.Command.JobFullName;

            try
            {
                runtime.Process = ManagedLocalProcess.Start(
                    command,
                    Path.GetFullPath(runtime.Manifest.WorkingDirectory, _loaded.BaseDirectory),
                    environment,
                    $"job/{runtime.Command.Name}/{runtime.Command.ElementId}",
                    Path.Combine(_state.LogsDirectory, $"{runtime.Command.JobFullName}.log"));
                runtime.Status = JobStatus.Running;
                runtime.StartTimestamp = DateTime.UtcNow.Ticks;
                runtime.StartedAt = DateTimeOffset.UtcNow;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[job/{runtime.Command.Name}/{runtime.Command.ElementId}] start failed: {exception.Message}");
                CompleteAttempt(runtime, exitCode: -1);
            }
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                foreach (JobRuntime runtime in _jobs.Values)
                {
                    ManagedLocalProcess? completedProcess = null;
                    LocalJobGateway? gatewayToDispose = null;
                    bool restart = false;
                    bool remove = false;
                    lock (runtime.Gate)
                    {
                        if (runtime.Process is { HasExited: true } process)
                        {
                            int exitCode = process.ExitCode ?? -1;
                            runtime.Process = null;
                            completedProcess = process;
                            CompleteAttempt(runtime, exitCode);
                        }

                        if (!runtime.Completed && runtime.Process is null &&
                            DateTimeOffset.UtcNow >= runtime.NextStart)
                        {
                            restart = true;
                        }

                        int ttl = runtime.Command.CreateJob.TtlSecondsAfterFinished;
                        if (runtime.Completed && runtime.FinishedAt.HasValue &&
                            DateTimeOffset.UtcNow - runtime.FinishedAt.Value >= TimeSpan.FromSeconds(ttl))
                        {
                            remove = true;
                        }
                    }

                    if (completedProcess is not null)
                        await completedProcess.DisposeAsync();
                    if (restart)
                        Start(runtime);
                    if (remove &&
                        _jobs.TryRemove(runtime.Command.JobFullName, out JobRuntime? removed))
                    {
                        lock (removed.Gate)
                        {
                            gatewayToDispose = removed.Gateway;
                            removed.Gateway = null;
                        }
                    }
                    if (gatewayToDispose is not null)
                        await gatewayToDispose.DisposeAsync();
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void CompleteAttempt(JobRuntime runtime, int exitCode)
    {
        if (exitCode == 0)
        {
            runtime.Status = JobStatus.Succeeded;
            runtime.Completed = true;
            runtime.FinishedAt = DateTimeOffset.UtcNow;
            return;
        }

        if (runtime.Attempts <= runtime.Command.CreateJob.BackoffLimit)
        {
            runtime.Attempts++;
            runtime.Status = JobStatus.Pending;
            runtime.NextStart = DateTimeOffset.UtcNow.AddSeconds(Math.Min(30, 1 << Math.Min(runtime.Attempts - 1, 5)));
            return;
        }

        runtime.Status = JobStatus.Failed;
        runtime.Completed = true;
        runtime.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static SlimFaasJobConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, LocalJobManifest> jobs)
    {
        var configurations = new Dictionary<string, SlimfaasJob>(StringComparer.OrdinalIgnoreCase);
        var schedules = new Dictionary<string, IList<ScheduleCreateJob>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, LocalJobManifest manifest) in jobs)
        {
            JobMetadata metadata = JobMetadataParser.Parse(manifest.Annotations);
            CreateJobResources resources = manifest.Resources is null
                ? CompatibilityResources
                : new CreateJobResources(manifest.Resources.Requests, manifest.Resources.Limits);
            IList<EnvVarInput> environment = manifest.Environment
                .Select(item => new EnvVarInput(item.Key, item.Value))
                .ToList();
            configurations[name] = new SlimfaasJob(
                Image: $"local:{name}",
                ImagesWhitelist: [],
                Resources: resources,
                DependsOn: metadata.DependsOn,
                Environments: environment,
                BackoffLimit: manifest.BackoffLimit,
                Visibility: metadata.Visibility.ToString(),
                NumberParallelJob: metadata.NumberParallelJob,
                TtlSecondsAfterFinished: manifest.TtlSecondsAfterFinished,
                RestartPolicy: manifest.RestartPolicy);

            if (metadata.Schedules.Count > 0)
            {
                schedules[name] = metadata.Schedules.Select(schedule => new ScheduleCreateJob(
                    Schedule: schedule.Schedule,
                    Args: schedule.Args ?? [],
                    Image: "",
                    BackoffLimit: schedule.BackoffLimit == 1
                        ? manifest.BackoffLimit
                        : schedule.BackoffLimit,
                    TtlSecondsAfterFinished: schedule.TtlSecondsAfterFinished == 60
                        ? manifest.TtlSecondsAfterFinished
                        : schedule.TtlSecondsAfterFinished,
                    RestartPolicy: schedule.RestartPolicy == "Never"
                        ? manifest.RestartPolicy
                        : schedule.RestartPolicy,
                    Resources: schedule.Resources ?? resources,
                    Environments: schedule.Environments ?? environment,
                    DependsOn: schedule.DependsOn ?? metadata.DependsOn)).ToList();
            }
        }

        return new SlimFaasJobConfiguration(configurations, schedules.Count > 0 ? schedules : null);
    }

    internal static string RewriteEntrypointUrls(
        string value,
        int entrypointPort,
        int gatewayPort)
    {
        string result = value;
        foreach (string scheme in new[] { "http", "ws" })
        {
            foreach (string host in new[] { "127.0.0.1", "localhost", "[::1]" })
            {
                result = result.Replace(
                    $"{scheme}://{host}:{entrypointPort}",
                    $"{scheme}://127.0.0.1:{gatewayPort}",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        if (_monitorTask is not null)
            await _monitorTask;

        foreach (string name in _jobs.Keys.ToArray())
            await DeleteAsync(name, CancellationToken.None);

        _stopping.Dispose();
    }

    private sealed class JobRuntime(ProcessCreateJobCommand command, LocalJobManifest manifest)
    {
        public object Gate { get; } = new();
        public ProcessCreateJobCommand Command { get; } = command;
        public LocalJobManifest Manifest { get; } = manifest;
        public ManagedLocalProcess? Process { get; set; }
        public LocalJobGateway? Gateway { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public int Attempts { get; set; } = 1;
        public long StartTimestamp { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset NextStart { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public bool Completed { get; set; }
    }
}
