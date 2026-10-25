using SlimFaas.Jobs;

namespace SlimFaas.Tests.Jobs;

public class CronTimeZoneTests
{
    [Theory(DisplayName = "GetLatestJobExecutionTimestamp – evaluates the cron fields in the schedule time zone")]
    // Cron,           TimeZone,           "Current UTC",           "Expected UTC"
    [InlineData("0 8 * * *",   null,               "2024-01-15T09:00:00Z", "2024-01-15T08:00:00Z")] // no time zone ⇒ UTC
    [InlineData("0 8 * * *",   "",                 "2024-01-15T09:00:00Z", "2024-01-15T08:00:00Z")] // empty ⇒ UTC
    [InlineData("0 8 * * *",   "UTC",              "2024-01-15T09:00:00Z", "2024-01-15T08:00:00Z")]
    [InlineData("0 8 * * *",   "Europe/Paris",     "2024-01-15T12:00:00Z", "2024-01-15T07:00:00Z")] // winter, UTC+1
    [InlineData("0 8 * * *",   "Europe/Paris",     "2024-07-15T12:00:00Z", "2024-07-15T06:00:00Z")] // summer, UTC+2
    [InlineData("0 8 * * *",   "Europe/Paris",     "2024-01-15T06:59:00Z", "2024-01-14T07:00:00Z")] // just before 08:00 Paris
    [InlineData("0 9 * * 1-5", "America/New_York", "2024-06-10T14:00:00Z", "2024-06-10T13:00:00Z")] // Monday, UTC-4
    [InlineData("0 1 * * 1",   "Asia/Tokyo",       "2024-06-10T00:00:00Z", "2024-06-09T16:00:00Z")] // Monday in Tokyo, Sunday in UTC
    [InlineData("0 0 1 * *",   "Asia/Tokyo",       "2024-06-01T00:00:00Z", "2024-05-31T15:00:00Z")] // 1st of the month in Tokyo
    [InlineData("30 8 * * *",  "Asia/Kolkata",     "2024-06-10T12:00:00Z", "2024-06-10T03:00:00Z")] // half-hour offset, UTC+5:30
    public void GetLatestJobExecutionTimestamp_UsesScheduleTimeZone(
        string cron, string? timeZone, string currentUtc, string expectedUtc)
    {
        var currentTs = DateTimeOffset.Parse(currentUtc).ToUnixTimeSeconds();
        var expectedTs = DateTimeOffset.Parse(expectedUtc).ToUnixTimeSeconds();

        var result = Cron.GetLatestJobExecutionTimestamp(cron, currentTs, timeZone);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedTs, result.Data);
    }

    [Theory(DisplayName = "GetNextJobExecutionTimestamp – evaluates the cron fields in the schedule time zone")]
    [InlineData("0 8 * * *",  "Europe/Paris", "2024-01-15T12:00:00Z", "2024-01-16T07:00:00Z")] // winter, UTC+1
    [InlineData("0 8 * * *",  "Europe/Paris", "2024-07-15T12:00:00Z", "2024-07-16T06:00:00Z")] // summer, UTC+2
    [InlineData("0 8 * * *",  "Europe/Paris", "2024-03-30T12:00:00Z", "2024-03-31T06:00:00Z")] // first day of summer time
    [InlineData("0 8 * * *",  "Europe/Paris", "2024-10-26T12:00:00Z", "2024-10-27T07:00:00Z")] // first day of winter time
    [InlineData("0 1 * * 1",  "Asia/Tokyo",   "2024-06-09T00:00:00Z", "2024-06-09T16:00:00Z")]
    public void GetNextJobExecutionTimestamp_UsesScheduleTimeZone(
        string cron, string timeZone, string currentUtc, string expectedUtc)
    {
        var currentTs = DateTimeOffset.Parse(currentUtc).ToUnixTimeSeconds();
        var expectedTs = DateTimeOffset.Parse(expectedUtc).ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp(cron, currentTs, timeZone);

        Assert.True(result.IsSuccess);
        Assert.Equal(expectedTs, result.Data);
    }

    // Europe/Paris moves from 02:00 CET to 03:00 CEST on 2024-03-31 at 01:00 UTC.
    [Theory(DisplayName = "Transition to summer time – a skipped local time does not run")]
    [InlineData("2024-03-31T12:00:00Z", "2024-03-30T01:30:00Z")] // 02:30 never happened on 2024-03-31
    [InlineData("2024-04-01T12:00:00Z", "2024-04-01T00:30:00Z")]
    public void GetLatestJobExecutionTimestamp_SkipsNonexistentLocalTime(string currentUtc, string expectedUtc)
    {
        var currentTs = DateTimeOffset.Parse(currentUtc).ToUnixTimeSeconds();
        var expectedTs = DateTimeOffset.Parse(expectedUtc).ToUnixTimeSeconds();

        var result = Cron.GetLatestJobExecutionTimestamp("30 2 * * *", currentTs, "Europe/Paris");

        Assert.Equal(expectedTs, result.Data);
    }

    [Fact(DisplayName = "Transition to summer time – the next run after a skipped local time is the next day")]
    public void GetNextJobExecutionTimestamp_SkipsNonexistentLocalTime()
    {
        var currentTs = DateTimeOffset.Parse("2024-03-30T12:00:00Z").ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("30 2 * * *", currentTs, "Europe/Paris");

        Assert.Equal(DateTimeOffset.Parse("2024-04-01T00:30:00Z").ToUnixTimeSeconds(), result.Data);
    }

    [Fact(DisplayName = "Transition to summer time – the first local time after the gap runs")]
    public void GetNextJobExecutionTimestamp_FirstLocalTimeAfterGap()
    {
        // 01:30 CET; 02:00 is skipped and 03:00 CEST is 01:00 UTC.
        var currentTs = DateTimeOffset.Parse("2024-03-31T00:30:00Z").ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("0 2,3 * * *", currentTs, "Europe/Paris");

        Assert.Equal(DateTimeOffset.Parse("2024-03-31T01:00:00Z").ToUnixTimeSeconds(), result.Data);
    }

    // Europe/Paris moves from 03:00 CEST back to 02:00 CET on 2024-10-27 at 01:00 UTC,
    // so 02:30 local time happens at 00:30 UTC and again at 01:30 UTC.
    [Theory(DisplayName = "Transition to winter time – a repeated local time runs at each occurrence")]
    [InlineData("2024-10-27T00:45:00Z", "2024-10-27T00:30:00Z")] // first 02:30, UTC+2
    [InlineData("2024-10-27T01:15:00Z", "2024-10-27T00:30:00Z")]
    [InlineData("2024-10-27T01:45:00Z", "2024-10-27T01:30:00Z")] // second 02:30, UTC+1
    public void GetLatestJobExecutionTimestamp_RepeatedLocalTime(string currentUtc, string expectedUtc)
    {
        var currentTs = DateTimeOffset.Parse(currentUtc).ToUnixTimeSeconds();
        var expectedTs = DateTimeOffset.Parse(expectedUtc).ToUnixTimeSeconds();

        var result = Cron.GetLatestJobExecutionTimestamp("30 2 * * *", currentTs, "Europe/Paris");

        Assert.Equal(expectedTs, result.Data);
    }

    [Fact(DisplayName = "Transition to winter time – the next run after the first occurrence is the second one")]
    public void GetNextJobExecutionTimestamp_RepeatedLocalTime()
    {
        var currentTs = DateTimeOffset.Parse("2024-10-27T00:45:00Z").ToUnixTimeSeconds();

        var result = Cron.GetNextJobExecutionTimestamp("30 2 * * *", currentTs, "Europe/Paris");

        Assert.Equal(DateTimeOffset.Parse("2024-10-27T01:30:00Z").ToUnixTimeSeconds(), result.Data);
    }

    [Theory(DisplayName = "An unknown time zone is rejected")]
    [InlineData("Invalid/Timezone")]
    [InlineData("+01:00")]
    [InlineData("CET+1")]
    [InlineData(" ")]
    [InlineData("europe/paris")]
    public void UnknownTimeZone_ReturnsInvalidTimeZone(string timeZone)
    {
        var ts = DateTimeOffset.Parse("2024-01-15T12:00:00Z").ToUnixTimeSeconds();

        var latest = Cron.GetLatestJobExecutionTimestamp("0 8 * * *", ts, timeZone);
        var next = Cron.GetNextJobExecutionTimestamp("0 8 * * *", ts, timeZone);

        Assert.Equal(Cron.InvalidTimeZone, latest.Error?.Key);
        Assert.Equal($"The timezone '{timeZone}' is not a valid IANA timezone.", latest.Error?.Description);
        Assert.Equal(Cron.InvalidTimeZone, next.Error?.Key);
    }
}
