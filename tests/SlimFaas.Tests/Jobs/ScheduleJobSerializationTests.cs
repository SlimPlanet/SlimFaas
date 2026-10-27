using MemoryPack;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Jobs;

public class ScheduleJobSerializationTests
{
    [Theory]
    [InlineData("UTC")]
    [InlineData("Europe/Paris")]
    public void Serialize_Explicit_TimeZone_Should_Preserve_Value_And_Distinct_Identity(string timeZone)
    {
        var schedule = new ScheduleCreateJob("0 8 * * *", ["arg"], TimeZone: timeZone);

        byte[] bytes = ScheduleJobSerialization.Serialize(schedule);

        var roundTrip = MemoryPackSerializer.Deserialize<ScheduleCreateJob>(bytes);
        Assert.NotNull(roundTrip);
        Assert.Equal(timeZone, roundTrip.TimeZone);
        Assert.Equal(schedule.Schedule, roundTrip.Schedule);
        Assert.Equal(schedule.Args, roundTrip.Args);
        Assert.NotEqual(ScheduleJobSerialization.GetId(schedule with { TimeZone = null }),
            ScheduleJobSerialization.GetId(schedule));
    }

    [Fact]
    public void GetId_Different_TimeZones_Should_Have_Different_Identities()
    {
        var schedule = new ScheduleCreateJob("0 8 * * *", ["arg"], TimeZone: "Europe/Paris");

        Assert.NotEqual(ScheduleJobSerialization.GetId(schedule),
            ScheduleJobSerialization.GetId(schedule with { TimeZone = "America/New_York" }));
    }
}
