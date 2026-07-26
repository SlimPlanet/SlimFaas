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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }
}
