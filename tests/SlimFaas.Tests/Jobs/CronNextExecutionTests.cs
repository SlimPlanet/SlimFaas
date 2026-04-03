using SlimFaas.Jobs;

namespace SlimFaas.Tests.Jobs;

public class CronNextExecutionTests
{
    [Fact(DisplayName = "GetNextJobExecutionTimestamp – every minute returns next minute")]
    public void EveryMinute_ReturnsNextMinute()
    {
        // 2026-01-15 12:30:00 UTC
        long ts = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("* * * * *", ts);

        Assert.True(result.IsSuccess);
        // Should be 12:31
        long expected = new DateTimeOffset(2026, 1, 15, 12, 31, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, result.Data);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – specific minute")]
    public void SpecificMinute_ReturnsCorrectTime()
    {
        // 2026-01-15 12:30:00 UTC -> next 45th minute = 12:45
        long ts = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("45 * * * *", ts);

        Assert.True(result.IsSuccess);
        long expected = new DateTimeOffset(2026, 1, 15, 12, 45, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, result.Data);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – wraps to next hour")]
    public void WrapsToNextHour()
    {
        // 2026-01-15 12:50:00 UTC, cron at minute 15 -> next is 13:15
        long ts = new DateTimeOffset(2026, 1, 15, 12, 50, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("15 * * * *", ts);

        Assert.True(result.IsSuccess);
        long expected = new DateTimeOffset(2026, 1, 15, 13, 15, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, result.Data);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – specific hour and minute")]
    public void SpecificHourAndMinute()
    {
        // 2026-01-15 12:30:00 UTC, cron "0 14 * * *" -> next is 14:00 same day
        long ts = new DateTimeOffset(2026, 1, 15, 12, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("0 14 * * *", ts);

        Assert.True(result.IsSuccess);
        long expected = new DateTimeOffset(2026, 1, 15, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, result.Data);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – wraps to next day")]
    public void WrapsToNextDay()
    {
        // 2026-01-15 15:00:00 UTC, cron "0 8 * * *" -> next is 2026-01-16 08:00
        long ts = new DateTimeOffset(2026, 1, 15, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("0 8 * * *", ts);

        Assert.True(result.IsSuccess);
        long expected = new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, result.Data);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – invalid cron returns error")]
    public void InvalidCron_ReturnsError()
    {
        long ts = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("invalid cron", ts);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – empty cron returns error")]
    public void EmptyCron_ReturnsError()
    {
        var result = Cron.GetNextJobExecutionTimestamp("", 1000);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact(DisplayName = "GetNextJobExecutionTimestamp – step expression */15")]
    public void StepExpression()
    {
        // 2026-01-15 12:02:00 UTC, cron "*/15 * * * *" -> next is 12:15
        long ts = new DateTimeOffset(2026, 1, 15, 12, 2, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("*/15 * * * *", ts);

        Assert.True(result.IsSuccess);
        long expected = new DateTimeOffset(2026, 1, 15, 12, 15, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        Assert.Equal(expected, result.Data);
    }
}

