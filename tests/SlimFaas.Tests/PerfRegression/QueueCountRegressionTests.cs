using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.PerfRegression;

// Non-regression tests protecting the queue counting semantics ("count-only reads"
// theme): whatever the internal path (element materialization or direct counting),
// CountElementAsync must stay consistent with the actual queue content and with the
// aggregated dispatch state.
public class QueueCountRegressionTests
{
    private static readonly RetryInformation RetryInformation = new([2, 4, 8], 30, [500, 502, 503]);

    [Fact]
    public async Task CountElementAsyncMatchesListElementsAsync()
    {
        var database = new DatabaseMockService();
        var queue = new SlimFaasQueue(database);

        for (var i = 0; i < 7; i++)
        {
            await queue.EnqueueAsync("fibonacci", [1, 2, 3, (byte)i], RetryInformation);
        }

        long count = await queue.CountElementAsync("fibonacci", [CountType.Available]);
        var elements = await queue.ListElementsAsync("fibonacci", [CountType.Available]);

        Assert.Equal(7, count);
        Assert.Equal(elements.Count, count);
    }

    [Fact]
    public async Task CountElementAsyncReturnsZeroForUnknownQueue()
    {
        var database = new DatabaseMockService();
        var queue = new SlimFaasQueue(database);

        long count = await queue.CountElementAsync("unknown", [CountType.Available]);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DispatchStateTotalsAreConsistentWithCounts()
    {
        var database = new DatabaseMockService();
        var queue = new SlimFaasQueue(database);
        for (var i = 0; i < 4; i++)
        {
            await queue.EnqueueAsync("fibonacci", [(byte)i], RetryInformation);
        }

        long availableCount = await queue.CountElementAsync("fibonacci", [CountType.Available]);
        QueueDispatchState dispatchState = await queue.GetDispatchStateAsync("fibonacci");

        Assert.Equal(availableCount, dispatchState.Available);
        Assert.Equal(
            dispatchState.Available + dispatchState.Running + dispatchState.WaitingForRetry,
            dispatchState.TotalPending);
    }
}
