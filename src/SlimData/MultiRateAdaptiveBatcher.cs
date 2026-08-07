using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace SlimData;

public sealed record RateTier(int MinPerMinute, TimeSpan Delay);

public readonly record struct AdaptiveBatchTiming(TimeSpan Delay, TimeSpan CoalesceWindow);

public readonly record struct AdaptiveBatchEnqueueOptions(TimeSpan? MaximumQueueWait = null);

public readonly record struct AdaptiveBatchQueueStatistics(
    string Kind,
    int Items,
    long Bytes,
    long LatencySensitiveOperations = 0,
    long ForcedFlushes = 0,
    double LastLatencySensitiveWaitMilliseconds = 0);

public sealed class BatchQueueFullException(string kind)
    : Exception($"Adaptive batch queue '{kind}' is full.")
{
    public string Kind { get; } = kind;
}

public sealed class BatchItemTooLargeException(string kind, long itemBytes, long maximumBytes)
    : Exception($"Adaptive batch item for '{kind}' is {itemBytes} bytes; maximum is {maximumBytes} bytes.")
{
    public string Kind { get; } = kind;
    public long ItemBytes { get; } = itemBytes;
    public long MaximumBytes { get; } = maximumBytes;
}

public sealed class MultiRateAdaptiveBatcher : IAsyncDisposable
{
    private sealed record PendingItem(
        object Request,
        TaskCompletionSource<object> Completion,
        int Size,
        long EnqueuedTimestamp,
        long? FlushDeadlineTimestamp);

    private sealed class Kind
    {
        public int QueueLength;
        public long QueueBytes;
        public int ArrivalsCount;
        public required string Name { get; init; }
        public required Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyList<object>>> Handler { get; init; }
        public required List<RateTier> Tiers { get; init; }
        public required int MaxBatchSize { get; init; }
        public int MaxQueueLength { get; init; }
        public long MaxQueueBytes { get; init; }
        public int MaxBatchBytes { get; init; }
        public TimeSpan CoalesceWindow { get; init; }
        public Func<AdaptiveBatchTiming>? TimingProvider { get; init; }
        public required Func<object, int> SizeEstimatorBytes { get; init; }
        public object QueueGate { get; } = new();
        public Queue<PendingItem> Queue { get; } = new();
        public ConcurrentQueue<long> Arrivals { get; } = new();
        public long NextAllowedDequeueTimestamp;
        public long? EarliestFlushDeadlineTimestamp;
        public long LatencySensitiveOperations;
        public long ForcedFlushes;
        public long LastLatencySensitiveWaitMicroseconds;
    }

    private sealed class VersionSignal
    {
        private readonly object _gate = new();
        private TaskCompletionSource _changed = CreateCompletion();
        private long _version;

        public long Version => Volatile.Read(ref _version);

        public void Pulse()
        {
            TaskCompletionSource changed;
            lock (_gate)
            {
                Interlocked.Increment(ref _version);
                changed = _changed;
                _changed = CreateCompletion();
            }
            changed.TrySetResult();
        }

        public async Task<long> WaitForChangeAsync(
            long observedVersion,
            TimeSpan timeout,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            Task task;
            lock (_gate)
            {
                if (_version != observedVersion)
                    return _version;
                task = _changed.Task;
            }

            try
            {
                await task.WaitAsync(timeout, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            return Version;
        }

        private static TaskCompletionSource CreateCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Kind> _kinds = new();
    private readonly VersionSignal _signal = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly TimeProvider _timeProvider;
    private volatile bool _disposed;
    private volatile int _workerRunning;
    private Task? _loopTask;

    public MultiRateAdaptiveBatcher(
        TimeSpan? idleStop = null,
        TimeSpan? maxWaitPerTick = null,
        TimeProvider? timeProvider = null)
    {
        IdleStop = idleStop ?? TimeSpan.FromSeconds(15);
        MaxWaitPerTick = maxWaitPerTick ?? TimeSpan.FromSeconds(5);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan IdleStop { get; }
    public TimeSpan MaxWaitPerTick { get; }

    public void RegisterKind<TReq, TRes>(
        string kind,
        Func<IReadOnlyList<TReq>, CancellationToken, Task<IReadOnlyList<TRes>>> batchHandler,
        IEnumerable<RateTier>? tiers = null,
        int maxBatchSize = 512,
        int maxQueueLength = 0,
        long maxQueueBytes = 0L,
        int maxBatchBytes = 512 * 1024 * 1024,
        Func<TReq, int>? sizeEstimatorBytes = null,
        TimeSpan? coalesceWindow = null,
        Func<AdaptiveBatchTiming>? timingProvider = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(batchHandler);

        var tiersList = (tiers ??
        [
            new RateTier(32, TimeSpan.FromMilliseconds(60)),
            new RateTier(64, TimeSpan.FromMilliseconds(120)),
            new RateTier(128, TimeSpan.FromMilliseconds(240))
        ]).OrderBy(tier => tier.MinPerMinute).ToList();
        Func<TReq, int> typedEstimator = sizeEstimatorBytes ?? (_ => 0);
        var registeredKind = new Kind
        {
            Name = kind,
            MaxBatchSize = Math.Max(1, maxBatchSize),
            MaxQueueLength = Math.Max(0, maxQueueLength),
            MaxQueueBytes = Math.Max(0L, maxQueueBytes),
            Tiers = tiersList,
            MaxBatchBytes = Math.Max(0, maxBatchBytes),
            SizeEstimatorBytes = request => typedEstimator((TReq)request),
            CoalesceWindow = coalesceWindow ?? tiersList[0].Delay,
            TimingProvider = timingProvider,
            Handler = async (requests, cancellationToken) =>
            {
                var typedRequests = requests.Cast<TReq>().ToArray();
                IReadOnlyList<TRes> results = await batchHandler(typedRequests, cancellationToken)
                    .ConfigureAwait(false);
                return results.Cast<object>().ToArray();
            }
        };

        if (!_kinds.TryAdd(kind, registeredKind))
            throw new InvalidOperationException($"Kind '{kind}' is already registered.");
    }

    public async Task<TRes> EnqueueAsync<TReq, TRes>(
        string kind,
        TReq request,
        CancellationToken cancellationToken = default,
        AdaptiveBatchEnqueueOptions enqueueOptions = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_kinds.TryGetValue(kind, out Kind? registeredKind))
            throw new KeyNotFoundException($"Kind '{kind}' is not registered.");
        if (enqueueOptions.MaximumQueueWait is { } maximumQueueWait && maximumQueueWait < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(enqueueOptions), "Maximum queue wait cannot be negative.");

        int size = Math.Max(0, registeredKind.SizeEstimatorBytes(request!));
        long maximumItemBytes = GetMaximumItemBytes(registeredKind);
        if (maximumItemBytes > 0L && size > maximumItemBytes)
            throw new BatchItemTooLargeException(kind, size, maximumItemBytes);

        long enqueuedTimestamp = _timeProvider.GetTimestamp();
        long? flushDeadline = enqueueOptions.MaximumQueueWait is { } wait
            ? AddTimestamp(enqueuedTimestamp, wait)
            : null;
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (registeredKind.QueueGate)
        {
            if ((registeredKind.MaxQueueLength > 0 &&
                 registeredKind.QueueLength >= registeredKind.MaxQueueLength) ||
                (registeredKind.MaxQueueBytes > 0L &&
                 registeredKind.QueueBytes + size > registeredKind.MaxQueueBytes))
            {
                throw new BatchQueueFullException(kind);
            }

            registeredKind.Queue.Enqueue(new PendingItem(
                request!, completion, size, enqueuedTimestamp, flushDeadline));
            registeredKind.QueueLength++;
            registeredKind.QueueBytes += size;
            if (flushDeadline.HasValue &&
                (!registeredKind.EarliestFlushDeadlineTimestamp.HasValue ||
                 flushDeadline.Value < registeredKind.EarliestFlushDeadlineTimestamp.Value))
            {
                registeredKind.EarliestFlushDeadlineTimestamp = flushDeadline;
            }
        }
        if (flushDeadline.HasValue)
            Interlocked.Increment(ref registeredKind.LatencySensitiveOperations);
        RecordArrival(registeredKind);

        StartWorkerIfNeeded();
        _signal.Pulse();

        using CancellationTokenRegistration registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        object result = await completion.Task.ConfigureAwait(false);
        return (TRes)result;
    }

    public IReadOnlyList<AdaptiveBatchQueueStatistics> GetQueueStatistics() =>
        _kinds.Values.Select(kind => new AdaptiveBatchQueueStatistics(
            kind.Name,
            Volatile.Read(ref kind.QueueLength),
            Volatile.Read(ref kind.QueueBytes),
            Volatile.Read(ref kind.LatencySensitiveOperations),
            Volatile.Read(ref kind.ForcedFlushes),
            Volatile.Read(ref kind.LastLatencySensitiveWaitMicroseconds) / 1000d)).ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StartWorkerIfNeeded()
    {
        if (_disposed)
            return;
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
            _loopTask = Task.Run(() => LoopAsync(_disposeCts.Token), _disposeCts.Token);
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        long observedVersion = _signal.Version;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (AllQueuesEmpty())
                {
                    long idleStarted = _timeProvider.GetTimestamp();
                    while (AllQueuesEmpty() &&
                           _timeProvider.GetElapsedTime(idleStarted) < IdleStop)
                    {
                        TimeSpan remaining = IdleStop - _timeProvider.GetElapsedTime(idleStarted);
                        observedVersion = await _signal.WaitForChangeAsync(
                            observedVersion,
                            Min(MaxWaitPerTick, Min(remaining, TimeSpan.FromMilliseconds(250))),
                            _timeProvider,
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (AllQueuesEmpty())
                    {
                        Interlocked.Exchange(ref _workerRunning, 0);
                        if (!AllQueuesEmpty() &&
                            Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
                        {
                            continue;
                        }
                        return;
                    }
                }

                long now = _timeProvider.GetTimestamp();
                Kind? readyKind = null;
                long? nextReadyTimestamp = null;
                bool deadlineForced = false;
                foreach (Kind kind in _kinds.Values)
                {
                    if (IsQueueEmpty(kind))
                        continue;
                    AdaptiveBatchTiming timing = GetTiming(kind);
                    if (timing.Delay <= TimeSpan.Zero)
                        kind.NextAllowedDequeueTimestamp = 0;

                    long normalReady = kind.NextAllowedDequeueTimestamp;
                    long? deadline = GetEarliestDeadline(kind);
                    bool normalIsReady = normalReady == 0 || normalReady <= now;
                    bool deadlineIsReady = deadline.HasValue && deadline.Value <= now;
                    if (normalIsReady || deadlineIsReady)
                    {
                        readyKind = kind;
                        deadlineForced = deadlineIsReady && !normalIsReady;
                        break;
                    }

                    long effectiveReady = deadline.HasValue
                        ? Math.Min(normalReady, deadline.Value)
                        : normalReady;
                    if (!nextReadyTimestamp.HasValue || effectiveReady < nextReadyTimestamp.Value)
                        nextReadyTimestamp = effectiveReady;
                }

                if (readyKind is null)
                {
                    TimeSpan wait = MaxWaitPerTick;
                    if (nextReadyTimestamp.HasValue)
                        wait = Min(wait, DurationUntil(now, nextReadyTimestamp.Value));
                    observedVersion = await _signal.WaitForChangeAsync(
                        observedVersion,
                        wait,
                        _timeProvider,
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (deadlineForced)
                    Interlocked.Increment(ref readyKind.ForcedFlushes);
                AdaptiveBatchTiming currentTiming = GetTiming(readyKind);
                TimeSpan coalesce = deadlineForced ? TimeSpan.Zero : currentTiming.CoalesceWindow;
                long? earliestDeadline = GetEarliestDeadline(readyKind);
                if (earliestDeadline.HasValue)
                    coalesce = Min(coalesce, DurationUntil(now, earliestDeadline.Value));
                if (coalesce > TimeSpan.Zero && GetQueueCount(readyKind) < readyKind.MaxBatchSize)
                    await Task.Delay(coalesce, _timeProvider, cancellationToken).ConfigureAwait(false);

                List<PendingItem> batch = DrainBatch(readyKind);
                if (batch.Count == 0)
                    continue;

                RecordLatencySensitiveWait(readyKind, batch);
                try
                {
                    IReadOnlyList<object> results = await readyKind.Handler(
                        batch.Select(item => item.Request).ToArray(), cancellationToken).ConfigureAwait(false);
                    if (results.Count != batch.Count)
                        throw new InvalidOperationException("Batch handler must return as many results as inputs.");
                    for (var index = 0; index < results.Count; index++)
                        batch[index].Completion.TrySetResult(results[index]);
                }
                catch (Exception exception)
                {
                    foreach (PendingItem item in batch)
                        item.Completion.TrySetException(exception);
                }

                TimeSpan delayAfterBatch = GetTiming(readyKind).Delay;
                readyKind.NextAllowedDequeueTimestamp = delayAfterBatch > TimeSpan.Zero
                    ? AddTimestamp(_timeProvider.GetTimestamp(), delayAfterBatch)
                    : 0;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            foreach (Kind kind in _kinds.Values)
            {
                while (TryDequeue(kind, out PendingItem? item))
                    item.Completion.TrySetException(new TaskCanceledException("Batcher stopped"));
            }
        }
    }

    private List<PendingItem> DrainBatch(Kind kind)
    {
        var batch = new List<PendingItem>(kind.MaxBatchSize);
        var usedBytes = 0;
        while (batch.Count < kind.MaxBatchSize)
        {
            PendingItem? next = Peek(kind);
            if (next is null)
                break;
            if (kind.MaxBatchBytes > 0 &&
                batch.Count > 0 &&
                usedBytes + next.Size > kind.MaxBatchBytes)
            {
                break;
            }
            if (!TryDequeue(kind, out PendingItem? accepted))
                continue;
            if (accepted.Completion.Task.IsCompleted)
                continue;
            batch.Add(accepted);
            usedBytes += accepted.Size;
            if (kind.MaxBatchBytes > 0 && batch.Count == 1 && usedBytes >= kind.MaxBatchBytes)
                break;
        }
        return batch;
    }

    private void RecordLatencySensitiveWait(Kind kind, IReadOnlyList<PendingItem> batch)
    {
        PendingItem? lastSensitive = batch.LastOrDefault(item => item.FlushDeadlineTimestamp.HasValue);
        if (lastSensitive is null)
            return;
        double milliseconds = _timeProvider.GetElapsedTime(lastSensitive.EnqueuedTimestamp).TotalMilliseconds;
        Volatile.Write(
            ref kind.LastLatencySensitiveWaitMicroseconds,
            (long)(Math.Max(0, milliseconds) * 1000d));
    }

    private PendingItem? Peek(Kind kind)
    {
        lock (kind.QueueGate)
            return kind.Queue.TryPeek(out PendingItem? item) ? item : null;
    }

    private static bool TryDequeue(Kind kind, out PendingItem item)
    {
        lock (kind.QueueGate)
        {
            if (!kind.Queue.TryDequeue(out item!))
                return false;
            kind.QueueLength--;
            kind.QueueBytes -= item.Size;
            if (item.FlushDeadlineTimestamp == kind.EarliestFlushDeadlineTimestamp)
                kind.EarliestFlushDeadlineTimestamp = FindEarliestDeadline(kind.Queue);
            return true;
        }
    }

    private static long? FindEarliestDeadline(IEnumerable<PendingItem> queue)
    {
        long? earliest = null;
        foreach (PendingItem item in queue)
        {
            if (item.FlushDeadlineTimestamp.HasValue &&
                (!earliest.HasValue || item.FlushDeadlineTimestamp.Value < earliest.Value))
            {
                earliest = item.FlushDeadlineTimestamp;
            }
        }
        return earliest;
    }

    private static int GetQueueCount(Kind kind)
    {
        lock (kind.QueueGate)
            return kind.Queue.Count;
    }

    private static bool IsQueueEmpty(Kind kind)
    {
        lock (kind.QueueGate)
            return kind.Queue.Count == 0;
    }

    private bool AllQueuesEmpty() => _kinds.Values.All(IsQueueEmpty);

    private static long? GetEarliestDeadline(Kind kind)
    {
        lock (kind.QueueGate)
            return kind.EarliestFlushDeadlineTimestamp;
    }

    private long AddTimestamp(long timestamp, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return timestamp;
        double delta = duration.TotalSeconds * _timeProvider.TimestampFrequency;
        return delta >= long.MaxValue - timestamp
            ? long.MaxValue
            : timestamp + (long)Math.Ceiling(delta);
    }

    private TimeSpan DurationUntil(long now, long timestamp)
    {
        if (timestamp <= now)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((timestamp - now) / (double)_timeProvider.TimestampFrequency);
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private static long GetMaximumItemBytes(Kind kind)
    {
        if (kind.MaxBatchBytes <= 0)
            return kind.MaxQueueBytes;
        if (kind.MaxQueueBytes <= 0L)
            return kind.MaxBatchBytes;
        return Math.Min(kind.MaxBatchBytes, kind.MaxQueueBytes);
    }

    private void RecordArrival(Kind kind)
    {
        kind.Arrivals.Enqueue(_timeProvider.GetTimestamp());
        Interlocked.Increment(ref kind.ArrivalsCount);
        PruneArrivals(kind);
    }

    private int ComputeRatePerMinute(Kind kind)
    {
        PruneArrivals(kind);
        return Math.Max(0, Volatile.Read(ref kind.ArrivalsCount));
    }

    private TimeSpan ComputeDelayFromRatePerMinute(Kind kind)
    {
        int requestsPerMinute = ComputeRatePerMinute(kind);
        TimeSpan delay = TimeSpan.Zero;
        foreach (RateTier tier in kind.Tiers)
        {
            if (requestsPerMinute > tier.MinPerMinute)
                delay = tier.Delay;
            else
                break;
        }
        return delay;
    }

    private AdaptiveBatchTiming GetTiming(Kind kind) =>
        kind.TimingProvider?.Invoke() ??
        new AdaptiveBatchTiming(ComputeDelayFromRatePerMinute(kind), kind.CoalesceWindow);

    private void PruneArrivals(Kind kind)
    {
        long now = _timeProvider.GetTimestamp();
        long window = (long)(_timeProvider.TimestampFrequency * 60d);
        while (kind.Arrivals.TryPeek(out long timestamp) && now - timestamp > window)
        {
            if (kind.Arrivals.TryDequeue(out _))
                Interlocked.Decrement(ref kind.ArrivalsCount);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _disposeCts.Cancel();
        _signal.Pulse();
        try
        {
            if (_loopTask is not null)
                await _loopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _disposeCts.Dispose();
    }
}
