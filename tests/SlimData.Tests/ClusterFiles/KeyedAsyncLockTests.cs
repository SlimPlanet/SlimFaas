using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class KeyedAsyncLockTests
{
    [Fact]
    public async Task AcquireAsync_rejects_when_pending_queue_is_full()
    {
        var sut = new KeyedAsyncLock(
            maxInFlightBytes: 10,
            maxPendingTransfers: 2,
            queueWaitTimeout: TimeSpan.FromSeconds(5));
        await using var holder = await sut.AcquireAsync("same", 10, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var first = sut.AcquireAsync("same", 1, cancellation.Token).AsTask();
        var second = sut.AcquireAsync("same", 1, cancellation.Token).AsTask();
        await WaitForPendingAsync(sut, 2);

        await Assert.ThrowsAsync<FileTransferCapacityExceededException>(
            async () => await sut.AcquireAsync("other", 1, CancellationToken.None));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(0, sut.PendingTransfers);
    }

    [Fact]
    public async Task AcquireAsync_times_out_without_leaking_reserved_bytes()
    {
        var sut = new KeyedAsyncLock(
            maxInFlightBytes: 10,
            maxPendingTransfers: 2,
            queueWaitTimeout: TimeSpan.FromMilliseconds(50));
        var holder = await sut.AcquireAsync("first", 10, CancellationToken.None);

        await Assert.ThrowsAsync<FileTransferCapacityExceededException>(
            async () => await sut.AcquireAsync("second", 10, CancellationToken.None));

        Assert.Equal(0, sut.PendingTransfers);
        Assert.Equal(10, sut.InFlightBytes);

        await holder.DisposeAsync();
        Assert.Equal(0, sut.InFlightBytes);
    }

    [Fact]
    public async Task Canceled_same_key_waiter_does_not_release_the_holder_semaphore()
    {
        var sut = new KeyedAsyncLock(
            maxInFlightBytes: 10,
            maxPendingTransfers: 4,
            queueWaitTimeout: TimeSpan.FromSeconds(5));
        var holder = await sut.AcquireAsync("same", 1, CancellationToken.None);
        using var canceled = new CancellationTokenSource();

        var canceledWaiter = sut.AcquireAsync("same", 1, canceled.Token).AsTask();
        await WaitForPendingAsync(sut, 1);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);

        var next = sut.AcquireAsync("same", 1, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.False(next.IsCompleted);

        await holder.DisposeAsync();
        await using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Releasing_transfer_wakes_waiter_and_restores_byte_budget()
    {
        var sut = new KeyedAsyncLock(
            maxInFlightBytes: 10,
            maxPendingTransfers: 2,
            queueWaitTimeout: TimeSpan.FromSeconds(2));
        var first = await sut.AcquireAsync("first", 10, CancellationToken.None);
        var waiting = sut.AcquireAsync("second", 10, CancellationToken.None).AsTask();
        await WaitForPendingAsync(sut, 1);

        await first.DisposeAsync();
        await using (var second = await waiting.WaitAsync(TimeSpan.FromSeconds(1)))
        {
            Assert.Equal(10, sut.InFlightBytes);
        }

        Assert.Equal(0, sut.InFlightBytes);
    }

    private static async Task WaitForPendingAsync(KeyedAsyncLock sut, int expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (sut.PendingTransfers != expected)
            await Task.Delay(1, timeout.Token);
    }
}
