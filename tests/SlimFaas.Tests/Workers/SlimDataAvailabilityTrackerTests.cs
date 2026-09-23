using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataAvailabilityTrackerTests
{
    [Fact]
    public void Leader_loss_is_observed_even_when_all_local_entries_are_applied()
    {
        var clock = new ManualClock();
        var availability = new SlimDataAvailabilityTracker(clock);
        var progress = new SlimDataProgressTracker(clock);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.None, availability.Observe(true, true));
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Unavailable, availability.Observe(false, false));
        clock.Advance(30);
        progress.Observe(100, 100);
        availability.Observe(false, false);
        Assert.False(progress.IsStalled);
        Assert.Equal(30, availability.UnavailableSeconds);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void Either_missing_leader_or_consensus_starts_the_outage(bool hasLeader, bool hasConsensus)
    {
        var clock = new ManualClock();
        var tracker = new SlimDataAvailabilityTracker(clock);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Unavailable, tracker.Observe(hasLeader, hasConsensus));
        clock.Advance(59);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.None, tracker.Observe(hasLeader, hasConsensus));
        Assert.Equal(59, tracker.UnavailableSeconds);
        clock.Advance(1);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Unavailable, tracker.Observe(hasLeader, hasConsensus));
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.None, tracker.Observe(hasLeader, hasConsensus));
        clock.Advance(60);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Unavailable, tracker.Observe(hasLeader, hasConsensus));
    }

    [Fact]
    public void Electing_a_leader_without_consensus_does_not_reset_the_outage()
    {
        var clock = new ManualClock();
        var tracker = new SlimDataAvailabilityTracker(clock);
        tracker.Observe(false, false);
        clock.Advance(20);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Unavailable, tracker.Observe(true, false));
        Assert.Equal(20, tracker.UnavailableSeconds);
        clock.Advance(20);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.None, tracker.Observe(true, false));
        Assert.Equal(40, tracker.UnavailableSeconds);
    }

    [Fact]
    public void Recovery_clears_the_duration_and_a_later_outage_starts_at_zero()
    {
        var clock = new ManualClock();
        var tracker = new SlimDataAvailabilityTracker(clock);
        tracker.Observe(false, false);
        clock.Advance(5);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Recovered, tracker.Observe(true, true));
        Assert.Equal(0, tracker.UnavailableSeconds);
        clock.Advance(120);
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.None, tracker.Observe(true, true));
        Assert.Equal(SlimDataAvailabilityTracker.AvailabilityChange.Unavailable, tracker.Observe(false, false));
        Assert.Equal(0, tracker.UnavailableSeconds);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        internal void Advance(long seconds) => _seconds += seconds;
    }
}
