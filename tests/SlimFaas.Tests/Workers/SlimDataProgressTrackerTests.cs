using System.Diagnostics;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataProgressTrackerTests
{
    private static long At(int seconds) => seconds * Stopwatch.Frequency;

    [Fact]
    public void Idle_time_does_not_count_toward_a_new_backlog()
    {
        var tracker = new SlimDataProgressTracker();
        Assert.False(tracker.Observe(10, 10, 10, false, At(0)));
        Assert.False(tracker.Observe(10, 10, 10, false, At(120)));
        Assert.False(tracker.Observe(11, 10, 10, false, At(120)));
        Assert.False(tracker.Observe(11, 10, 10, false, At(149)));
        Assert.False(tracker.IsStalled);
        Assert.True(tracker.Observe(11, 10, 10, false, At(150)));
        Assert.True(tracker.IsStalled);
    }

    [Fact]
    public void Growing_backlog_stalls_once_and_applying_an_entry_clears_it()
    {
        var tracker = new SlimDataProgressTracker();
        tracker.Observe(11, 10, 10, false, At(0));
        Assert.True(tracker.Observe(100, 10, 10, false, At(30)));
        Assert.True(tracker.IsStalled);
        Assert.False(tracker.Observe(200, 10, 10, false, At(90)));
        Assert.True(tracker.IsStalled);
        Assert.True(tracker.Observe(200, 11, 10, false, At(90)));
        Assert.False(tracker.IsStalled);
        Assert.False(tracker.Observe(200, 11, 11, false, At(119)));
        Assert.True(tracker.Observe(200, 11, 11, false, At(120)));
    }

    [Fact]
    public void Regular_progress_prevents_a_stall_even_when_the_backlog_grows()
    {
        var tracker = new SlimDataProgressTracker();
        for (int i = 0; i < 10; i++)
        {
            Assert.False(tracker.Observe(100 + i * 10, i, i - 1, false, At(i * 25)));
            Assert.False(tracker.IsStalled);
        }
    }

    [Theory]
    [InlineData(10, 10L)] // Backlog drained or truncated.
    [InlineData(20, null)] // An implementation without an applied index.
    [InlineData(10, 20L)] // Independently sampled indices may cross.
    public void Clearing_or_losing_the_backlog_resets_the_observation(long lastIndex, long? appliedIndex)
    {
        var tracker = new SlimDataProgressTracker();
        tracker.Observe(20, 10, 10, false, At(0));
        tracker.Observe(20, 10, 10, false, At(30));
        Assert.True(tracker.Observe(lastIndex, appliedIndex, 10, false, At(30)));
        Assert.False(tracker.IsStalled);
        Assert.False(tracker.Observe(21, 10, 10, false, At(150)));
        Assert.False(tracker.IsStalled);
    }

    [Theory]
    [InlineData(19, 10, true)]
    [InlineData(20, 9, false)]
    public void Rewinding_either_index_starts_a_new_observation(long lastIndex, long appliedIndex, bool logRewound)
    {
        var tracker = new SlimDataProgressTracker();
        tracker.Observe(20, 10, 10, false, At(0));
        tracker.Observe(20, 10, 10, false, At(30));
        Assert.True(tracker.Observe(lastIndex, appliedIndex, 10, logRewound, At(30)));
        Assert.False(tracker.IsStalled);
        Assert.True(tracker.Observe(lastIndex, appliedIndex, appliedIndex, false, At(60)));
    }
}
