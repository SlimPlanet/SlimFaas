using SlimData;
using Microsoft.Extensions.Logging;
using Moq;

namespace SlimData.Tests;

public sealed class MultiRateAdaptiveBatcherRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static readonly AdaptiveBatchTiming Immediate = new(TimeSpan.Zero, TimeSpan.Zero);

    [Fact]
    public async Task Crossing_idle_deadline_between_clock_reads_allows_retirement_and_next_command()
    {
        var time = new CrossingDeadlineTimeProvider();
        await using var batcher = new MultiRateAdaptiveBatcher(timeProvider: time);
        var calls = 0;
        batcher.RegisterKind<int, int>("commands", (requests, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                time.Arm();
            return Task.FromResult<IReadOnlyList<int>>(requests.ToArray());
        }, timingProvider: () => Immediate);

        Assert.Equal(1, await batcher.EnqueueAsync<int, int>("commands", 1).WaitAsync(Deadline));
        await batcher.WorkerTask!.WaitAsync(Deadline);
        Assert.Equal(2, await batcher.EnqueueAsync<int, int>("commands", 2).WaitAsync(Deadline));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Unexpected_scheduler_failure_completes_pending_command_and_allows_restart()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(value => value.IsEnabled(LogLevel.Error)).Returns(true);
        await using var batcher = new MultiRateAdaptiveBatcher(logger: logger.Object);
        var failure = new InvalidOperationException("Injected scheduler failure");
        var failNext = 1;
        batcher.RegisterKind<int, int>("commands",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.ToArray()),
            timingProvider: () => Interlocked.Exchange(ref failNext, 0) == 1 ? throw failure : Immediate);

        Task<int> pending = batcher.EnqueueAsync<int, int>("commands", 1);
        Task worker = batcher.WorkerTask!;
        Exception observed = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Deadline));
        Assert.Same(failure, observed);
        await worker.WaitAsync(Deadline);
        logger.Verify(value => value.Log(LogLevel.Error, It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), failure,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        Assert.Equal(0, Assert.Single(batcher.GetQueueStatistics()).Items);
        Assert.Equal(2, await batcher.EnqueueAsync<int, int>("commands", 2).WaitAsync(Deadline));
    }

    [Fact]
    public async Task Failure_after_dequeue_completes_owned_batch_without_replaying_it()
    {
        var time = new FailingTimestampTimeProvider();
        await using var batcher = new MultiRateAdaptiveBatcher(timeProvider: time);
        var timingReads = 0;
        var handled = new List<int>();
        batcher.RegisterKind<int, int>("commands", (requests, _) =>
        {
            handled.AddRange(requests);
            return Task.FromResult<IReadOnlyList<int>>(requests.ToArray());
        }, timingProvider: () =>
        {
            // The second timing read precedes draining; the next timestamp read
            // records queue latency for the batch that has already been removed.
            if (Interlocked.Increment(ref timingReads) == 2)
                time.FailNextRead();
            return Immediate;
        });

        Task<int> pending = batcher.EnqueueAsync<int, int>("commands", 1,
            new AdaptiveBatchEnqueueOptions(TimeSpan.FromSeconds(1)));
        Task worker = batcher.WorkerTask!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Deadline));
        await worker.WaitAsync(Deadline);
        Assert.Empty(handled);
        Assert.Equal(0, Assert.Single(batcher.GetQueueStatistics()).Items);
        Assert.Equal(2, await batcher.EnqueueAsync<int, int>("commands", 2).WaitAsync(Deadline));
        Assert.Equal(new[] { 2 }, handled);
    }

    [Fact]
    public async Task Failure_after_write_preserves_result_fails_backlog_and_does_not_replay()
    {
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Injected post-write scheduler failure");
        var timingReads = 0;
        var handled = new List<int>();
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<int, int>("commands", async (requests, _) =>
        {
            handled.AddRange(requests);
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
            return requests.ToArray();
        }, maxBatchSize: 1, maxQueueLength: 2, maxQueueBytes: 2,
            sizeEstimatorBytes: _ => 1,
            timingProvider: () => Interlocked.Increment(ref timingReads) == 3 ? throw failure : Immediate);

        Task<int> written = batcher.EnqueueAsync<int, int>("commands", 1);
        await handlerEntered.Task.WaitAsync(Deadline);
        Task worker = batcher.WorkerTask!;
        Task<int> second = batcher.EnqueueAsync<int, int>("commands", 2);
        Task<int> third = batcher.EnqueueAsync<int, int>("commands", 3);
        try
        {
            await Assert.ThrowsAsync<BatchQueueFullException>(() => batcher.EnqueueAsync<int, int>("commands", 4));
        }
        finally
        {
            releaseHandler.TrySetResult();
        }
        Assert.Equal(1, await written.WaitAsync(Deadline));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Deadline)));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => third.WaitAsync(Deadline)));
        await worker.WaitAsync(Deadline);
        AdaptiveBatchQueueStatistics statistics = Assert.Single(batcher.GetQueueStatistics());
        Assert.Equal(0, statistics.Items);
        Assert.Equal(0, statistics.Bytes);

        Assert.Equal(4, await batcher.EnqueueAsync<int, int>("commands", 4).WaitAsync(Deadline));
        Assert.Equal(new[] { 1, 4 }, handled);
    }

    [Fact]
    public async Task Enqueue_racing_with_disposal_is_rejected_instead_of_waiting_forever()
    {
        var estimatorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEstimator = new ManualResetEventSlim();
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<int, int>("commands",
            (requests, _) => Task.FromResult<IReadOnlyList<int>>(requests.ToArray()),
            sizeEstimatorBytes: _ =>
            {
                estimatorEntered.TrySetResult();
                if (!releaseEstimator.Wait(Deadline))
                    throw new TimeoutException("Test did not release the estimator");
                return 1;
            }, timingProvider: () => Immediate);

        Task<int> pending = Task.Run(() => batcher.EnqueueAsync<int, int>("commands", 1));
        try
        {
            await estimatorEntered.Task.WaitAsync(Deadline);
            await batcher.DisposeAsync();
        }
        finally
        {
            releaseEstimator.Set();
        }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(Deadline));
        Assert.Equal(0, Assert.Single(batcher.GetQueueStatistics()).Items);
    }

    [Fact]
    public async Task Concurrent_disposals_both_wait_for_the_active_handler()
    {
        var handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var batcher = new MultiRateAdaptiveBatcher();
        batcher.RegisterKind<int, int>("commands", async (requests, _) =>
        {
            handlerEntered.TrySetResult();
            await releaseHandler.Task;
            return requests.ToArray();
        }, timingProvider: () => Immediate);

        Task<int> pending = batcher.EnqueueAsync<int, int>("commands", 1);
        await handlerEntered.Task.WaitAsync(Deadline);
        Task first = batcher.DisposeAsync().AsTask();
        Task second = batcher.DisposeAsync().AsTask();
        try
        {
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            releaseHandler.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Deadline);
        }
        Assert.Equal(1, await pending.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Command_arriving_at_idle_retirement_is_processed_once()
    {
        using var releaseDeadline = new ManualResetEventSlim();
        var deadlineRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new RetirementTimeProvider(deadlineRead, releaseDeadline);
        await using var batcher = new MultiRateAdaptiveBatcher(timeProvider: time);
        var activeHandlers = 0;
        var calls = 0;
        batcher.RegisterKind<int, int>("commands", (requests, _) =>
        {
            Assert.Equal(1, Interlocked.Increment(ref activeHandlers));
            if (Interlocked.Increment(ref calls) == 1)
                time.Arm();
            Interlocked.Decrement(ref activeHandlers);
            return Task.FromResult<IReadOnlyList<int>>(requests.ToArray());
        }, timingProvider: () => Immediate);

        Assert.Equal(1, await batcher.EnqueueAsync<int, int>("commands", 1).WaitAsync(Deadline));
        Task<int> next;
        try
        {
            await deadlineRead.Task.WaitAsync(Deadline);
            next = batcher.EnqueueAsync<int, int>("commands", 2);
        }
        finally
        {
            releaseDeadline.Set();
        }
        Assert.Equal(2, await next.WaitAsync(Deadline));
        Assert.Equal(2, calls);
        Assert.Equal(0, Assert.Single(batcher.GetQueueStatistics()).Items);
    }

    private sealed class CrossingDeadlineTimeProvider : TimeProvider
    {
        private int _armed;
        private int _reads;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Arm() => Volatile.Write(ref _armed, 1);

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _armed) == 0)
                return 0;
            int read = Interlocked.Increment(ref _reads);
            return read switch
            {
                1 => 0, // idle interval starts
                2 => TimeSpan.FromMilliseconds(14_999).Ticks,
                _ => TimeSpan.FromMilliseconds(15_010L * (read - 2)).Ticks
            };
        }

        // The fixed worker waits once for the last millisecond, then reads the
        // expired deadline. The old worker throws before it can create this timer.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private sealed class FailingTimestampTimeProvider : TimeProvider
    {
        private int _failNext;
        public void FailNextRead() => Volatile.Write(ref _failNext, 1);
        public override long GetTimestamp() => Interlocked.Exchange(ref _failNext, 0) == 1
            ? throw new InvalidOperationException("Injected latency measurement failure")
            : System.GetTimestamp();
    }

    private sealed class RetirementTimeProvider(
        TaskCompletionSource deadlineRead, ManualResetEventSlim releaseDeadline) : TimeProvider
    {
        private int _armed;
        private int _reads;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Arm() => Volatile.Write(ref _armed, 1);

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref _armed) == 0)
                return 0;
            int read = Interlocked.Increment(ref _reads);
            if (read == 2)
            {
                deadlineRead.TrySetResult();
                if (!releaseDeadline.Wait(Deadline))
                    throw new TimeoutException("Test did not release idle retirement");
            }
            return TimeSpan.FromSeconds(16L * (read - 1)).Ticks;
        }
    }
}
