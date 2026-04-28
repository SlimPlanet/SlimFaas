using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Options;

namespace SlimFaas.Endpoints;

public interface IStatusStreamSnapshotCache
{
    Task<string> GetStateFrameAsync(bool includeRecentActivity, CancellationToken ct);
}

public sealed class StatusStreamSnapshotCache : IStatusStreamSnapshotCache
{
    private static readonly CountType[] QueueCountTypes =
    [
        CountType.Available,
        CountType.Running,
        CountType.WaitingForRetry
    ];

    private readonly IReplicasService _replicasService;
    private readonly FunctionStatusCache _functionStatusCache;
    private readonly NetworkActivityTracker _activityTracker;
    private readonly ISlimFaasQueue _slimFaasQueue;
    private readonly IOptions<SlimFaasOptions> _slimFaasOptions;
    private readonly IJobConfiguration? _jobConfiguration;
    private readonly IJobService? _jobService;
    private readonly IScheduleJobService? _scheduleJobService;
    private readonly ILogger<StatusStreamSnapshotCache> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private readonly SemaphoreSlim _queuesLock = new(1, 1);
    private readonly SemaphoreSlim _jobsLock = new(1, 1);

    private string? _cachedStateFrameWithoutRecentActivity;
    private DateTimeOffset _stateExpiresAt;

    private IList<QueueInfo>? _cachedQueues;
    private string _cachedQueuesKey = string.Empty;
    private DateTimeOffset _queuesExpiresAt;

    private IList<JobConfigurationStatus>? _cachedJobs;
    private DateTimeOffset _jobsExpiresAt;

    public StatusStreamSnapshotCache(
        IReplicasService replicasService,
        FunctionStatusCache functionStatusCache,
        NetworkActivityTracker activityTracker,
        ISlimFaasQueue slimFaasQueue,
        IOptions<SlimFaasOptions> slimFaasOptions,
        ILogger<StatusStreamSnapshotCache> logger,
        IJobConfiguration? jobConfiguration = null,
        IJobService? jobService = null,
        IScheduleJobService? scheduleJobService = null)
    {
        _replicasService = replicasService;
        _functionStatusCache = functionStatusCache;
        _activityTracker = activityTracker;
        _slimFaasQueue = slimFaasQueue;
        _slimFaasOptions = slimFaasOptions;
        _logger = logger;
        _jobConfiguration = jobConfiguration;
        _jobService = jobService;
        _scheduleJobService = scheduleJobService;
    }

    public async Task<string> GetStateFrameAsync(bool includeRecentActivity, CancellationToken ct)
    {
        if (includeRecentActivity)
        {
            return await BuildStateFrameAsync(includeRecentActivity: true, ct).ConfigureAwait(false);
        }

        var now = DateTimeOffset.UtcNow;
        var cached = Volatile.Read(ref _cachedStateFrameWithoutRecentActivity);
        if (cached is not null && now < _stateExpiresAt)
        {
            return cached;
        }

        await _stateLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedStateFrameWithoutRecentActivity is not null && now < _stateExpiresAt)
            {
                return _cachedStateFrameWithoutRecentActivity;
            }

            var frame = await BuildStateFrameAsync(includeRecentActivity: false, ct).ConfigureAwait(false);
            _cachedStateFrameWithoutRecentActivity = frame;
            _stateExpiresAt = now.AddMilliseconds(Math.Max(1, _slimFaasOptions.Value.StatusStream.StateIntervalMilliseconds));
            return frame;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private async Task<string> BuildStateFrameAsync(bool includeRecentActivity, CancellationToken ct)
    {
        var functions = _functionStatusCache.GetAllDetailed(_replicasService);
        var queues = await GetQueuesAsync(functions, ct).ConfigureAwait(false);
        var jobs = await GetJobsAsync(ct).ConfigureAwait(false);
        var slimFaasInfo = _replicasService.Deployments.SlimFaas;
        var slimFaasNodes = slimFaasInfo.Pods
            .Select(p => new SlimFaasNodeInfo(
                p.Name,
                p.Ready == true ? "Running" : (p.Started == true ? "Starting" : "Pending")))
            .ToList();

        var payload = new StatusStreamPayload(
            Functions: functions,
            Queues: queues,
            Jobs: jobs,
            RecentActivity: includeRecentActivity ? _activityTracker.GetRecent() : Array.Empty<NetworkActivityEvent>(),
            SlimFaasReplicas: slimFaasInfo.Replicas,
            SlimFaasNodes: slimFaasNodes,
            FrontEnabled: _slimFaasOptions.Value.EnableFront,
            FrontMessage: _slimFaasOptions.Value.EnableFront ? null : "SlimFaas front is disabled by configuration (SlimFaas:EnableFront=false).");

        string json = JsonSerializer.Serialize(payload, StatusStreamSerializerContext.Default.StatusStreamPayload);
        return $"event: state\ndata: {json}\n\n";
    }

    private async Task<IList<QueueInfo>> GetQueuesAsync(IReadOnlyList<FunctionStatusDetailed> functions, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var now = DateTimeOffset.UtcNow;
        var key = string.Join('\u001f', functions.Select(f => f.Name).OrderBy(name => name, StringComparer.Ordinal));
        var ttlMs = _slimFaasOptions.Value.StatusStream.QueueLengthsCacheMilliseconds;

        if (ttlMs > 0 && _cachedQueues is not null && key == _cachedQueuesKey && now < _queuesExpiresAt)
        {
            return _cachedQueues;
        }

        await _queuesLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (ttlMs > 0 && _cachedQueues is not null && key == _cachedQueuesKey && now < _queuesExpiresAt)
            {
                return _cachedQueues;
            }

            var queues = new List<QueueInfo>(functions.Count);
            foreach (var fn in functions)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    long length = await _slimFaasQueue.CountElementAsync(fn.Name, QueueCountTypes).ConfigureAwait(false);
                    queues.Add(new QueueInfo(fn.Name, length));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Unable to read queue length for function {FunctionName}.", fn.Name);
                    queues.Add(new QueueInfo(fn.Name, 0));
                }
            }

            _cachedQueues = queues;
            _cachedQueuesKey = key;
            _queuesExpiresAt = ttlMs <= 0 ? DateTimeOffset.MinValue : now.AddMilliseconds(ttlMs);
            return queues;
        }
        finally
        {
            _queuesLock.Release();
        }
    }

    private async Task<IList<JobConfigurationStatus>> GetJobsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (_jobConfiguration is null || _jobService is null)
        {
            return Array.Empty<JobConfigurationStatus>();
        }

        var now = DateTimeOffset.UtcNow;
        var ttlMs = _slimFaasOptions.Value.StatusStream.JobsCacheMilliseconds;
        if (ttlMs > 0 && _cachedJobs is not null && now < _jobsExpiresAt)
        {
            return _cachedJobs;
        }

        await _jobsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (ttlMs > 0 && _cachedJobs is not null && now < _jobsExpiresAt)
            {
                return _cachedJobs;
            }

            try
            {
                _cachedJobs = await JobStatusEndpoints.BuildJobStatusesAsync(
                    _jobConfiguration,
                    _jobService,
                    _scheduleJobService).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to build jobs snapshot for status stream.");
                _cachedJobs = Array.Empty<JobConfigurationStatus>();
            }

            _jobsExpiresAt = ttlMs <= 0 ? DateTimeOffset.MinValue : now.AddMilliseconds(ttlMs);
            return _cachedJobs;
        }
        finally
        {
            _jobsLock.Release();
        }
    }
}


