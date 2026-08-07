using System.Collections.Immutable;
using Moq;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed class QueueDispatchStateTests
{
    [Fact]
    public void Builds_consistent_lightweight_state_in_one_pass()
    {
        long now = DateTime.UtcNow.Ticks;
        ImmutableArray<QueueElement> queue =
        [
            Element("available", new byte[2 * 1024 * 1024]),
            Element(
                "running",
                [1],
                retries: [new QueueHttpTryElement(now, reservedIp: "10.0.0.7")]),
            Element(
                "retry",
                [2],
                httpTimeoutSeconds: 1,
                timeoutRetriesSeconds: [10],
                retries: [new QueueHttpTryElement(now - TimeSpan.FromSeconds(2).Ticks)]),
            Element(
                "finished",
                [3],
                retries: [new QueueHttpTryElement(now - 1, endTimeStamp: now, httpCode: 204)])
        ];

        QueueDispatchState state = SlimDataService.BuildQueueDispatchState(queue, now);

        Assert.Equal(1, state.Available);
        Assert.Equal(1, state.Running);
        Assert.Equal(1, state.WaitingForRetry);
        Assert.Equal(3, state.TotalPending);
        QueueRunningReservation reservation = Assert.Single(state.RunningReservations);
        Assert.Equal("running", reservation.Id);
        Assert.Equal("10.0.0.7", reservation.ReservedIp);
        Assert.DoesNotContain(
            typeof(QueueDispatchState).GetProperties(),
            property => property.PropertyType == typeof(byte[]));
    }

    [Fact]
    public async Task Concurrent_snapshots_do_not_mix_queue_states()
    {
        long now = DateTime.UtcNow.Ticks;
        ImmutableArray<QueueElement> queue = Enumerable.Range(0, 1_000)
            .Select(index => Element($"available-{index}", [1]))
            .ToImmutableArray();

        QueueDispatchState[] states = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => SlimDataService.BuildQueueDispatchState(queue, now))));

        Assert.All(states, state =>
        {
            Assert.Equal(1_000, state.Available);
            Assert.Equal(0, state.Running);
            Assert.Equal(0, state.WaitingForRetry);
        });
    }

    [Fact]
    public async Task Queue_adapter_uses_dispatch_snapshot_without_legacy_payload_reads()
    {
        var expected = new QueueDispatchState(
            2,
            1,
            3,
            [new QueueRunningReservation("id", "10.0.0.1")]);
        var database = new Mock<IDatabaseService>(MockBehavior.Strict);
        database.Setup(service => service.GetQueueDispatchStateAsync("Queue:function"))
            .ReturnsAsync(expected);
        var queue = new SlimFaasQueue(database.Object);

        QueueDispatchState actual = await queue.GetDispatchStateAsync("function");

        Assert.Same(expected, actual);
        database.Verify(
            service => service.ListCountElementAsync(
                It.IsAny<string>(),
                It.IsAny<IList<CountType>>(),
                It.IsAny<int>()),
            Times.Never);
    }

    private static QueueElement Element(
        string id,
        byte[] payload,
        int httpTimeoutSeconds = 30,
        ImmutableArray<int> timeoutRetriesSeconds = default,
        ImmutableArray<QueueHttpTryElement> retries = default) =>
        new(
            payload,
            id,
            DateTime.UtcNow.Ticks,
            httpTimeoutSeconds,
            timeoutRetriesSeconds.IsDefault ? [] : timeoutRetriesSeconds,
            retries.IsDefault ? [] : retries,
            ImmutableHashSet<int>.Empty);
}
