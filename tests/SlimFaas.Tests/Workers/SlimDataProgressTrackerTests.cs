using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataProgressTrackerTests
{
    [Fact]
    public void Idle_time_does_not_count_toward_a_new_backlog()
    {
        var clock = new ManualClock();
        var tracker = new SlimDataProgressTracker(clock);
        Assert.False(tracker.Observe(10, 10));
        clock.Advance(120);
        Assert.False(tracker.Observe(10, 10));
        Assert.False(tracker.Observe(11, 10));
        clock.Advance(29);
        Assert.False(tracker.Observe(11, 10));
        Assert.False(tracker.IsStalled);
        clock.Advance(1);
        Assert.True(tracker.Observe(11, 10));
        Assert.True(tracker.IsStalled);
    }

    [Fact]
    public void Growing_backlog_stalls_once_and_applying_an_entry_clears_it()
    {
        var clock = new ManualClock();
        var tracker = new SlimDataProgressTracker(clock);
        tracker.Observe(11, 10);
        clock.Advance(30);
        Assert.True(tracker.Observe(100, 10));
        Assert.True(tracker.IsStalled);
        clock.Advance(60);
        Assert.False(tracker.Observe(200, 10));
        Assert.True(tracker.IsStalled);
        Assert.True(tracker.Observe(200, 11));
        Assert.False(tracker.IsStalled);
        clock.Advance(29);
        Assert.False(tracker.Observe(200, 11));
        clock.Advance(1);
        Assert.True(tracker.Observe(200, 11));
    }

    [Fact]
    public void Regular_progress_prevents_a_stall_even_when_the_backlog_grows()
    {
        var clock = new ManualClock();
        var tracker = new SlimDataProgressTracker(clock);
        for (var i = 0; i < 10; i++)
        {
            Assert.False(tracker.Observe(100 + i * 10, i));
            Assert.False(tracker.IsStalled);
            clock.Advance(25);
        }
    }

    [Theory]
    [InlineData(10, 10L)] // Backlog drained or truncated.
    [InlineData(20, null)] // An implementation without an applied index.
    [InlineData(20, -1L)] // Invalid observation.
    [InlineData(10, 20L)] // Independently sampled indices may cross.
    public void Clearing_or_losing_the_backlog_resets_the_observation(long lastIndex, long? appliedIndex)
    {
        var clock = new ManualClock();
        var tracker = new SlimDataProgressTracker(clock);
        tracker.Observe(20, 10);
        clock.Advance(30);
        tracker.Observe(20, 10);
        Assert.True(tracker.Observe(lastIndex, appliedIndex));
        Assert.False(tracker.IsStalled);
        clock.Advance(120);
        Assert.False(tracker.Observe(21, 10));
        Assert.False(tracker.IsStalled);
    }

    [Theory]
    [InlineData(19, 10)]
    [InlineData(20, 9)]
    public void Rewinding_either_index_starts_a_new_observation(long lastIndex, long appliedIndex)
    {
        var clock = new ManualClock();
        var tracker = new SlimDataProgressTracker(clock);
        tracker.Observe(20, 10);
        clock.Advance(30);
        tracker.Observe(20, 10);
        Assert.True(tracker.Observe(lastIndex, appliedIndex));
        Assert.False(tracker.IsStalled);
        clock.Advance(30);
        Assert.True(tracker.Observe(lastIndex, appliedIndex));
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        internal void Advance(long seconds) => _seconds += seconds;
    }
}
