namespace SlimFaas.Tests;

public class HistoryHttpMemoryServiceTests
{
    [Fact]
    public void GetTicksLastCall_DefaultsToZero()
    {
        HistoryHttpMemoryService historyHttpMemoryService = new HistoryHttpMemoryService();
        Assert.Equal(0L, historyHttpMemoryService.GetTicksLastCall("test"));
    }

    [Fact]
    public void GetTicksLastCall_RetrievesCachedValue()
    {
        HistoryHttpMemoryService historyHttpMemoryService = new HistoryHttpMemoryService();
        historyHttpMemoryService.SetTickLastCall("test", 1L);
        Assert.Equal(1L, historyHttpMemoryService.GetTicksLastCall("test"));
    }

    [Fact]
    public void GetTicksLastCall_ConcurrentWrites()
    {
        HistoryHttpMemoryService historyHttpMemoryService = new HistoryHttpMemoryService();
        Parallel.For(0, 1000, i => { historyHttpMemoryService.SetTickLastCall("test", i); });
        Assert.True(historyHttpMemoryService.GetTicksLastCall("test") < 1000);
        Assert.True(historyHttpMemoryService.GetTicksLastCall("test") >= 0);
    }

    [Fact]
    public void ActiveCalls_AreCountedAndCanRefreshTheLastCallTimestamp()
    {
        var history = new HistoryHttpMemoryService();

        history.BeginActiveCall("test");
        history.BeginActiveCall("test");
        history.RefreshActiveCall("test", 1234L);

        Assert.True(history.HasActiveCalls("test"));
        Assert.Equal(1234L, history.GetTicksLastCall("test"));

        history.EndActiveCall("test");
        Assert.True(history.HasActiveCalls("test"));

        history.EndActiveCall("test");
        Assert.False(history.HasActiveCalls("test"));

        long completedTicks = history.GetTicksLastCall("test");
        history.RefreshActiveCall("test", completedTicks + 1);
        Assert.Equal(completedTicks, history.GetTicksLastCall("test"));
    }
}
