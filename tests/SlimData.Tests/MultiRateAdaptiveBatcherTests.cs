using SlimData;

namespace SlimData.Tests;

public sealed class MultiRateAdaptiveBatcherTests
{
    [Fact]
    public async Task Rejects_item_larger_than_queue_byte_limit()
    {
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<byte[], int>(
            "bytes",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.Select(static request => request.Length).ToArray()),
            maxQueueBytes: 10,
            sizeEstimatorBytes: static request => request.Length);

        var exception = await Assert.ThrowsAsync<BatchItemTooLargeException>(() =>
            batcher.EnqueueAsync<byte[], int>("bytes", new byte[11]));

        Assert.Equal(11, exception.ItemBytes);
        Assert.Equal(10, exception.MaximumBytes);
    }

    [Fact]
    public async Task Rejects_item_larger_than_batch_byte_limit()
    {
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<byte[], int>(
            "bytes",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.Select(static request => request.Length).ToArray()),
            maxQueueBytes: 100,
            maxBatchBytes: 10,
            sizeEstimatorBytes: static request => request.Length);

        var exception = await Assert.ThrowsAsync<BatchItemTooLargeException>(() =>
            batcher.EnqueueAsync<byte[], int>("bytes", new byte[11]));

        Assert.Equal(11, exception.ItemBytes);
        Assert.Equal(10, exception.MaximumBytes);
    }

    [Fact]
    public async Task Bounds_waiting_items_and_releases_queue_accounting()
    {
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<byte[], int>(
            "bytes",
            async (requests, token) =>
            {
                handlerEntered.TrySetResult();
                await releaseHandler.Task.WaitAsync(token);
                return requests.Select(static request => request.Length).ToArray();
            },
            tiers: [new RateTier(0, TimeSpan.Zero)],
            maxBatchSize: 1,
            maxQueueLength: 1,
            maxQueueBytes: 8,
            maxBatchBytes: 8,
            sizeEstimatorBytes: static request => request.Length,
            coalesceWindow: TimeSpan.Zero);

        var first = batcher.EnqueueAsync<byte[], int>("bytes", new byte[4]);
        await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = batcher.EnqueueAsync<byte[], int>("bytes", new byte[4]);

        await Assert.ThrowsAsync<BatchQueueFullException>(() =>
            batcher.EnqueueAsync<byte[], int>("bytes", new byte[1]));

        var queued = Assert.Single(batcher.GetQueueStatistics());
        Assert.Equal(1, queued.Items);
        Assert.Equal(4, queued.Bytes);

        releaseHandler.TrySetResult();
        Assert.Equal(4, await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(4, await second.WaitAsync(TimeSpan.FromSeconds(5)));

        await WaitUntilAsync(() => batcher.GetQueueStatistics().Single().Items == 0);
        var drained = Assert.Single(batcher.GetQueueStatistics());
        Assert.Equal(0, drained.Items);
        Assert.Equal(0, drained.Bytes);
    }

    [Fact]
    public async Task Canceled_waiting_item_releases_queue_accounting()
    {
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<byte[], int>(
            "bytes",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.Select(static request => request.Length).ToArray()),
            tiers: [new RateTier(0, TimeSpan.Zero)],
            maxQueueLength: 4,
            maxQueueBytes: 16,
            maxBatchBytes: 16,
            sizeEstimatorBytes: static request => request.Length,
            coalesceWindow: TimeSpan.FromMilliseconds(100));

        using var cancellation = new CancellationTokenSource();
        var pending = batcher.EnqueueAsync<byte[], int>("bytes", new byte[4], cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitUntilAsync(() => batcher.GetQueueStatistics().Single().Items == 0);

        var drained = Assert.Single(batcher.GetQueueStatistics());
        Assert.Equal(0, drained.Items);
        Assert.Equal(0, drained.Bytes);
    }

    [Fact]
    public async Task Dynamic_timing_provider_can_remove_local_delay_and_coalescence()
    {
        var timing = new AdaptiveBatchTiming(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(100));
        var batchSizes = new List<int>();
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<int, int>(
            "dynamic",
            (requests, _) =>
            {
                lock (batchSizes)
                    batchSizes.Add(requests.Count);
                return Task.FromResult<IReadOnlyList<int>>(requests.ToArray());
            },
            tiers: [new RateTier(0, TimeSpan.FromMilliseconds(100))],
            maxBatchSize: 16,
            coalesceWindow: TimeSpan.FromMilliseconds(100),
            timingProvider: () => timing);

        var first = batcher.EnqueueAsync<int, int>("dynamic", 1);
        var second = batcher.EnqueueAsync<int, int>("dynamic", 2);
        var firstBatchResults = await Task
            .WhenAll(first, second)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2 }, firstBatchResults);
        Assert.Equal(2, Assert.Single(batchSizes));

        timing = new AdaptiveBatchTiming(TimeSpan.Zero, TimeSpan.Zero);
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.Equal(3, await batcher
            .EnqueueAsync<int, int>("dynamic", 3)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);

        Assert.True(
            elapsed < TimeSpan.FromMilliseconds(75),
            $"Expected the fast path below 75 ms, measured {elapsed.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    public async Task Latency_sensitive_item_flushes_fifo_prefix_at_its_deadline()
    {
        var time = new ManualTimeProvider();
        var batches = new List<int[]>();
        await using var batcher = new MultiRateAdaptiveBatcher(timeProvider: time);
        batcher.RegisterKind<int, int>(
            "commands",
            (requests, _) =>
            {
                lock (batches)
                    batches.Add(requests.ToArray());
                return Task.FromResult<IReadOnlyList<int>>(requests.ToArray());
            },
            tiers: [new RateTier(0, TimeSpan.FromMilliseconds(100))],
            coalesceWindow: TimeSpan.Zero,
            timingProvider: () => new AdaptiveBatchTiming(TimeSpan.FromMilliseconds(100), TimeSpan.Zero));

        Assert.Equal(1, await batcher.EnqueueAsync<int, int>("commands", 1).WaitAsync(TimeSpan.FromSeconds(5)));
        Task<int> normal = batcher.EnqueueAsync<int, int>("commands", 2);
        Task<int> urgent = batcher.EnqueueAsync<int, int>(
            "commands",
            3,
            enqueueOptions: new AdaptiveBatchEnqueueOptions(TimeSpan.FromMilliseconds(5)));
        await DrainContinuationsAsync();

        time.Advance(TimeSpan.FromMilliseconds(4));
        await DrainContinuationsAsync();
        Assert.False(normal.IsCompleted);
        Assert.False(urgent.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        int[] completed = await Task.WhenAll(normal, urgent).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 2, 3 }, completed);
        Assert.Equal(new[] { 1 }, batches[0]);
        Assert.Equal(new[] { 2, 3 }, batches[1]);
        AdaptiveBatchQueueStatistics statistics = Assert.Single(batcher.GetQueueStatistics());
        Assert.Equal(1, statistics.LatencySensitiveOperations);
        Assert.Equal(1, statistics.ForcedFlushes);
        Assert.InRange(statistics.LastLatencySensitiveWaitMilliseconds, 5, 5.001);
    }

    [Fact]
    public async Task Latency_sensitive_deadline_caps_coalescence_window()
    {
        var time = new ManualTimeProvider();
        await using var batcher = new MultiRateAdaptiveBatcher(timeProvider: time);
        batcher.RegisterKind<int, int>(
            "commands",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.ToArray()),
            tiers: [new RateTier(0, TimeSpan.Zero)],
            coalesceWindow: TimeSpan.FromMilliseconds(100),
            timingProvider: () => new AdaptiveBatchTiming(TimeSpan.Zero, TimeSpan.FromMilliseconds(100)));

        Task<int> pending = batcher.EnqueueAsync<int, int>(
            "commands",
            7,
            enqueueOptions: new AdaptiveBatchEnqueueOptions(TimeSpan.FromMilliseconds(5)));
        await DrainContinuationsAsync();
        time.Advance(TimeSpan.FromMilliseconds(4));
        await DrainContinuationsAsync();
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(7, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Non_sensitive_item_keeps_adaptive_cooldown()
    {
        var time = new ManualTimeProvider();
        await using var batcher = new MultiRateAdaptiveBatcher(timeProvider: time);
        batcher.RegisterKind<int, int>(
            "commands",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.ToArray()),
            tiers: [new RateTier(0, TimeSpan.FromMilliseconds(100))],
            coalesceWindow: TimeSpan.Zero,
            timingProvider: () => new AdaptiveBatchTiming(TimeSpan.FromMilliseconds(100), TimeSpan.Zero));

        Assert.Equal(1, await batcher.EnqueueAsync<int, int>("commands", 1).WaitAsync(TimeSpan.FromSeconds(5)));
        Task<int> pending = batcher.EnqueueAsync<int, int>("commands", 2);
        await DrainContinuationsAsync();
        time.Advance(TimeSpan.FromMilliseconds(99));
        await DrainContinuationsAsync();
        Assert.False(pending.IsCompleted);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Rejects_negative_latency_sensitive_deadline()
    {
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<int, int>(
            "commands",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.ToArray()));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => batcher.EnqueueAsync<int, int>(
            "commands",
            1,
            enqueueOptions: new AdaptiveBatchEnqueueOptions(TimeSpan.FromMilliseconds(-1))));
    }

    private static async Task DrainContinuationsAsync()
    {
        for (var index = 0; index < 10; index++)
            await Task.Yield();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
                return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate)
                return _timestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.ChangeCore(dueTime, period, _timestamp);
            }
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(duration));
            lock (_gate)
            {
                _timestamp += duration.Ticks;
                _utcNow += duration;
            }

            while (true)
            {
                ManualTimer[] due;
                lock (_gate)
                    due = _timers.Where(timer => timer.IsDue(_timestamp)).ToArray();
                if (due.Length == 0)
                    return;
                foreach (ManualTimer timer in due)
                    timer.Fire(_timestamp);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
                _timers.Remove(timer);
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private readonly object _gate = new();
            private long _dueTimestamp = long.MaxValue;
            private long _periodTicks = Timeout.InfiniteTimeSpan.Ticks;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                    return ChangeCore(dueTime, period, owner._timestamp);
            }

            public bool ChangeCore(TimeSpan dueTime, TimeSpan period, long now)
            {
                lock (_gate)
                {
                    if (_disposed)
                        return false;
                    _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                        ? long.MaxValue
                        : checked(now + Math.Max(0, dueTime.Ticks));
                    _periodTicks = period.Ticks;
                    return true;
                }
            }

            public bool IsDue(long now)
            {
                lock (_gate)
                    return !_disposed && _dueTimestamp <= now;
            }

            public void Fire(long now)
            {
                lock (_gate)
                {
                    if (_disposed || _dueTimestamp > now)
                        return;
                    _dueTimestamp = _periodTicks > 0
                        ? checked(now + _periodTicks)
                        : long.MaxValue;
                }
                callback(state);
            }

            public void Dispose()
            {
                lock (_gate)
                    _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
