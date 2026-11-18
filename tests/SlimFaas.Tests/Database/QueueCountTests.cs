using System.Collections.Immutable;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed class QueueCountTests
{
    [Fact]
    public void Counts_and_classification_match_selections_at_timeout_and_retry_boundaries()
    {
        long now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var elements = ImmutableArray.CreateBuilder<QueueElement>();
        foreach (int retryCount in new[] { 0, 1, 2 })
        foreach (int tryCount in new[] { 0, 1, 2, 3 })
        foreach (int code in new[] { 200, 503 })
        foreach (long elapsed in new[] { -1L, 0, 1, 9_999_999, 10_000_000, 10_000_001, 29_999_999, 30_000_000, 30_000_001 })
        foreach (bool ended in new[] { false, true })
        {
            var element = new QueueElement(new byte[] { 1 }, "item", now, 1,
                Enumerable.Repeat(2, retryCount).ToImmutableArray(),
                Enumerable.Range(0, tryCount).Select(_ => new QueueHttpTryElement(
                    now - elapsed, "transaction", ended ? now - elapsed : 0, code)).ToImmutableArray(), [503]);
            var state = element.GetState(now);
            Assert.Equal(element.IsRunning(now), state == QueueElementState.Running);
            Assert.Equal(element.IsFinished(now), state == QueueElementState.Finished);
            Assert.Equal(element.IsWaitingForRetry(now), state == QueueElementState.WaitingForRetry);
            Assert.Equal(!element.IsFinished(now) && !element.IsRunning(now) && !element.IsWaitingForRetry(now),
                state == QueueElementState.Available);
            elements.Add(element);
        }

        var queue = elements.ToImmutable();
        for (int flags = 0; flags < 8; flags++)
        foreach (int maximum in new[] { -1, 0, 1, 10, int.MaxValue })
        {
            var types = new List<CountType>();
            if ((flags & 1) != 0) types.Add(CountType.Available);
            if ((flags & 2) != 0) types.Add(CountType.Running);
            if ((flags & 4) != 0) types.Add(CountType.WaitingForRetry);
            // Duplicate requests have always counted a category only once.
            types.AddRange(types.ToArray());
            long expected = (types.Contains(CountType.Available) ? queue.GetQueueAvailableElement(now, maximum).Length : 0) +
                (types.Contains(CountType.Running) ? queue.GetQueueRunningElement(now).Length : 0) +
                (types.Contains(CountType.WaitingForRetry) ? queue.GetQueueWaitingForRetryElement(now).Length : 0);
            Assert.Equal(expected, SlimDataService.CountQueueElements(queue, types, maximum, now));
            Assert.Equal(0, SlimDataService.CountQueueElements([], types, maximum, now));
        }
        var dispatch = SlimDataService.BuildQueueDispatchState(queue, now);
        Assert.Equal(queue.GetQueueAvailableElement(now, int.MaxValue).Length, dispatch.Available);
        Assert.Equal(queue.GetQueueRunningElement(now).Select(item => item.Id), dispatch.RunningReservations.Select(item => item.Id));
        Assert.Equal(queue.GetQueueWaitingForRetryElement(now).Length, dispatch.WaitingForRetry);
    }
}
