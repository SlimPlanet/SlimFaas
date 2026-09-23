using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed class SlimDataReadinessLogLimiterTests
{
    [Fact]
    public void Repeated_waiters_share_one_warning_per_minute()
    {
        var clock = new ManualClock();
        var limiter = new SlimDataReadinessLogLimiter(clock);
        Assert.True(limiter.ShouldLog("No leader"));
        for (int i = 0; i < 120; i++)
            Assert.False(limiter.ShouldLog("No leader"));
        clock.Advance(59);
        Assert.False(limiter.ShouldLog("No leader"));
        clock.Advance(1);
        Assert.True(limiter.ShouldLog("No leader"));
    }

    [Fact]
    public void Reason_changes_and_a_new_outage_are_reported_immediately()
    {
        var limiter = new SlimDataReadinessLogLimiter(new ManualClock());
        Assert.True(limiter.ShouldLog("No leader"));
        Assert.True(limiter.ShouldLog("Protocol unavailable"));
        Assert.False(limiter.ShouldLog("Protocol unavailable"));
        limiter.Reset();
        Assert.True(limiter.ShouldLog("Protocol unavailable"));
    }

    [Fact]
    public void Concurrent_waiters_do_not_multiply_warnings()
    {
        var limiter = new SlimDataReadinessLogLimiter(new ManualClock());
        int warnings = 0;
        Parallel.For(0, 100, _ =>
        {
            if (limiter.ShouldLog("No leader"))
                Interlocked.Increment(ref warnings);
        });
        Assert.Equal(1, warnings);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _seconds;
        internal void Advance(long seconds) => _seconds += seconds;
    }
}
