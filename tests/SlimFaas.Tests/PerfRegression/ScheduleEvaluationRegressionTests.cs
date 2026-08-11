using NodaTime;
using NodaTime.TimeZones;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.PerfRegression;

// Non-regression tests protecting the wake-up schedule evaluation ("recurring
// worker cost" theme): the result must stay correct and perfectly stable from one
// call to the next, whatever the internal time-zone resolution strategy (direct
// resolution or caching).
public class ScheduleEvaluationRegressionTests
{
    private static DeploymentInformation BuildFunction(string timeZoneId, params string[] wakeUps) =>
        new(
            "function-schedule",
            "unit-test",
            new List<PodInformation>(),
            new SlimFaasConfiguration(),
            Replicas: 1,
            Schedule: new ScheduleConfig
            {
                TimeZoneID = timeZoneId,
                Default = new DefaultSchedule { WakeUp = [.. wakeUps] }
            });

    [Fact]
    public void LastTicksMatchesDirectNodaTimeComputation()
    {
        var function = BuildFunction("Europe/Paris", "07:00");
        // August 11, 2026 14:00 UTC = 16:00 in Paris: the 07:00 wake-up has passed.
        var nowUtc = new DateTime(2026, 8, 11, 14, 0, 0, DateTimeKind.Utc);

        long? ticks = ReplicasService.GetLastTicksFromSchedule(function, nowUtc);

        Assert.NotNull(ticks);
        DateTimeZone zone = TzdbDateTimeZoneSource.Default.ForId("Europe/Paris");
        var expectedUtc = new LocalDateTime(2026, 8, 11, 7, 0)
            .InZoneLeniently(zone)
            .ToDateTimeUtc();
        Assert.Equal(expectedUtc.Ticks, ticks!.Value);
    }

    [Fact]
    public void RepeatedEvaluationsReturnTheSameResult()
    {
        var function = BuildFunction("Europe/Paris", "07:00", "12:30", "18:00");
        var nowUtc = new DateTime(2026, 8, 11, 14, 0, 0, DateTimeKind.Utc);

        long? first = ReplicasService.GetLastTicksFromSchedule(function, nowUtc);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(first, ReplicasService.GetLastTicksFromSchedule(function, nowUtc));
        }
    }

    [Fact]
    public void DifferentTimeZonesAreEvaluatedIndependently()
    {
        var nowUtc = new DateTime(2026, 8, 11, 14, 0, 0, DateTimeKind.Utc);

        long? paris = ReplicasService.GetLastTicksFromSchedule(BuildFunction("Europe/Paris", "07:00"), nowUtc);
        long? tokyo = ReplicasService.GetLastTicksFromSchedule(BuildFunction("Asia/Tokyo", "07:00"), nowUtc);

        Assert.NotNull(paris);
        Assert.NotNull(tokyo);
        Assert.NotEqual(paris, tokyo);
    }
}
