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
    private readonly LocalJobStore _store;
    private readonly object _creationGate = new();
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
        _store = new LocalJobStore(state.DirectoryPath);
        Configuration = BuildConfiguration(loaded.Manifest.Jobs);
    }

    public SlimFaasJobConfiguration Configuration { get; }
    public Task Completion => _monitorTask ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_creationGate)
        {
            foreach (LocalJobState saved in _store.ReadAll())
            {
                if (!_loaded.Manifest.Jobs.TryGetValue(saved.Command.Name, out LocalJobManifest? manifest))
                {
                    if (saved.Status is not (JobStatus.Succeeded or JobStatus.Failed))
                        throw new LocalManifestException($"Persisted job configuration '{saved.Command.Name}' no longer exists.");
                    manifest = new LocalJobManifest();
                }
                var runtime = new JobRuntime(saved.Command, manifest)
                {
                    Status = saved.Status == JobStatus.Running ? JobStatus.Pending : saved.Status,
                    Attempts = saved.Attempts,
                    StartTimestamp = saved.StartTimestamp,
                    NextStart = saved.NextStart,
                    FinishedAt = saved.FinishedAt,
                    Completed = saved.Status is JobStatus.Succeeded or JobStatus.Failed,
                    Deleted = saved.Deleted
                };
                _jobs.TryAdd(saved.Command.JobFullName, runtime);
                _knownJobNames.TryAdd(saved.Command.JobFullName, 0);
            }
        }
        _monitorTask = MonitorLoopAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public Task CreateAsync(ProcessCreateJobCommand command, CancellationToken cancellationToken)
    {
        if (!_loaded.Manifest.Jobs.TryGetValue(command.Name, out LocalJobManifest? manifest))
            throw new LocalManifestException($"Job configuration '{command.Name}' does not exist.");

        cancellationToken.ThrowIfCancellationRequested();
        // Persist the plan before the distributed queue can acknowledge it.
        // Serialize duplicate creates until their first durable publication.
        lock (_creationGate)
        {
            if (_knownJobNames.ContainsKey(command.JobFullName))
                return Task.CompletedTask;
            var runtime = new JobRuntime(command, manifest);
            _store.Save(runtime.ToState());
            _jobs.TryAdd(command.JobFullName, runtime);
            _knownJobNames.TryAdd(command.JobFullName, 0);
        }
        return Task.CompletedTask;
    }

    public IList<Job> Snapshot()
        => _jobs.Values
            .OrderByDescending(runtime => runtime.Command.InQueueTimestamp)
            .Select(runtime =>
            {
                lock (runtime.Gate)
                {
                    Configuration.Configurations.TryGetValue(runtime.Command.Name, out SlimfaasJob? configuration);
                    List<string> dependsOn = runtime.Command.CreateJob.DependsOn ?? configuration?.DependsOn ?? [];
                    return runtime.Deleted ? null : new Job(
                        Name: runtime.Command.JobFullName,
                        Status: runtime.NeedsPersistence ? JobStatus.Pending : runtime.Status,
                        Ips: [],
                        DependsOn: dependsOn,
                        ElementId: runtime.Command.ElementId,
                        InQueueTimestamp: runtime.Command.InQueueTimestamp,
                        StartTimestamp: runtime.StartTimestamp);
                }
            })
            .OfType<Job>()
            .ToList();

    public async Task DeleteAsync(string fullName, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(fullName, out JobRuntime? runtime))
            return;

        lock (runtime.Gate)
        {
            LocalJobState deleted = runtime.ToState() with
            {
                Deleted = true, Status = JobStatus.Failed, FinishedAt = DateTimeOffset.UtcNow
            };
            Persist(runtime, deleted);
            runtime.Deleted = true;
            runtime.Completed = true;
            runtime.Status = JobStatus.Failed;
            runtime.FinishedAt = deleted.FinishedAt;
        }
        await StopRuntimeAsync(runtime, cancellationToken);
    }

    private static async Task StopRuntimeAsync(JobRuntime runtime, CancellationToken cancellationToken)
    {
        ManagedLocalProcess? process;
        LocalWorkloadGateway? gateway;
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

            runtime.Gateway ??= new LocalWorkloadGateway(
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

            DateTimeOffset started = DateTimeOffset.UtcNow;
            Persist(runtime, runtime.ToState() with { Status = JobStatus.Running, StartTimestamp = started.UtcTicks });
            runtime.Status = JobStatus.Running;
            runtime.StartTimestamp = started.UtcTicks;
            runtime.StartedAt = started;
            try
            {
                runtime.Process = ManagedLocalProcess.Start(
                    command,
                    Path.GetFullPath(runtime.Manifest.WorkingDirectory, _loaded.BaseDirectory),
                    environment,
                    $"job/{runtime.Command.Name}/{runtime.Command.ElementId}",
                    Path.Combine(_state.LogsDirectory, $"{runtime.Command.JobFullName}.log"));
            }
            catch (Exception exception)
            {
                ILogger logger = LocalProcessOutputLog.ConsoleLogger;
                if (logger.IsEnabled(LogLevel.Error))
                    logger.LogLocalJobStartFailed(runtime.Command.Name, runtime.Command.ElementId, exception.ToString());
                CompleteAttempt(runtime, exitCode: -1);
                Persist(runtime);
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
                    await MonitorJobAsync(runtime);
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task MonitorJobAsync(JobRuntime runtime)
    {
        ManagedLocalProcess? completedProcess = null;
        LocalWorkloadGateway? gatewayToDispose = null;
        try
        {
            completedProcess = TakeCompletedProcess(runtime);
            bool restart;
            bool remove;
            lock (runtime.Gate)
            {
                if (runtime.NeedsPersistence)
                    Persist(runtime);
                restart = !runtime.Completed && runtime.Process is null && DateTimeOffset.UtcNow >= runtime.NextStart;
                int ttl = runtime.Command.CreateJob.TtlSecondsAfterFinished;
                remove = runtime.Completed && runtime.FinishedAt.HasValue &&
                         DateTimeOffset.UtcNow - runtime.FinishedAt.Value >= TimeSpan.FromSeconds(ttl);
            }
            if (completedProcess is not null)
            {
                ManagedLocalProcess exited = completedProcess;
                completedProcess = null;
                await exited.DisposeAsync();
            }
            if (restart)
                Start(runtime);
            if (remove)
            {
                lock (runtime.Gate)
                {
                    _store.Delete(runtime.Command.JobFullName);
                    PersistenceResumed(runtime);
                    if (_jobs.TryRemove(runtime.Command.JobFullName, out _))
                    {
                        gatewayToDispose = runtime.Gateway;
                        runtime.Gateway = null;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (runtime.Gate)
            {
                runtime.PersistenceFailures++;
                ILogger logger = LocalProcessOutputLog.ConsoleLogger;
                if (runtime.PersistenceFailures == 1 && logger.IsEnabled(LogLevel.Error))
                    logger.LogLocalJobPersistenceUnavailable(runtime.Command.JobFullName, exception.ToString());
            }
        }
        finally
        {
#pragma warning disable CA1508 // The detached process remains owned here when persisting its observed exit throws.
            if (completedProcess is not null)
                await completedProcess.DisposeAsync();
#pragma warning restore CA1508
            if (gatewayToDispose is not null)
                await gatewayToDispose.DisposeAsync();
        }
    }

    private static ManagedLocalProcess? TakeCompletedProcess(JobRuntime runtime)
    {
        lock (runtime.Gate)
        {
            if (runtime.Process is not { HasExited: true } process)
                return null;
            runtime.Process = null;
            CompleteAttempt(runtime, process.ExitCode ?? -1);
            return process;
        }
    }

    private void Persist(JobRuntime runtime, LocalJobState? state = null)
    {
        _store.Save(state ?? runtime.ToState());
        runtime.NeedsPersistence = false;
        PersistenceResumed(runtime);
    }

    private static void PersistenceResumed(JobRuntime runtime)
    {
        if (runtime.PersistenceFailures == 0)
            return;
        LocalProcessOutputLog.ConsoleLogger.LogLocalJobPersistenceResumed(runtime.Command.JobFullName, runtime.PersistenceFailures);
        runtime.PersistenceFailures = 0;
    }

    private static void CompleteAttempt(JobRuntime runtime, int exitCode)
    {
        runtime.NeedsPersistence = true;
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
                    DependsOn: schedule.DependsOn ?? metadata.DependsOn,
                    TimeZone: schedule.TimeZone)).ToList();
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
        try
        {
            if (_monitorTask is not null)
                await _monitorTask;
        }
        finally
        {
            try
            {
                await Task.WhenAll(_jobs.Values.Select(runtime => StopRuntimeAsync(runtime, CancellationToken.None)));
            }
            finally
            {
                _stopping.Dispose();
            }
        }
    }

    private sealed class JobRuntime(ProcessCreateJobCommand command, LocalJobManifest manifest)
    {
        public object Gate { get; } = new();
        public ProcessCreateJobCommand Command { get; } = command;
        public LocalJobManifest Manifest { get; } = manifest;
        public ManagedLocalProcess? Process { get; set; }
        public LocalWorkloadGateway? Gateway { get; set; }
        public JobStatus Status { get; set; } = JobStatus.Pending;
        public int Attempts { get; set; } = 1;
        public long StartTimestamp { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset NextStart { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public bool Completed { get; set; }
        public bool Deleted { get; set; }
        public bool NeedsPersistence { get; set; }
        public long PersistenceFailures { get; set; }

        public LocalJobState ToState() => new(1, Command, Status, Attempts, StartTimestamp, NextStart, FinishedAt, Deleted);
    }
}
