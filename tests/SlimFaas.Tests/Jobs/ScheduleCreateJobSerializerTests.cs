using MemoryPack;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Jobs;

public class ScheduleCreateJobSerializerTests
{
    private static readonly CreateJobResources Resources = new(
        new Dictionary<string, string> { { "cpu", "100m" } },
        new Dictionary<string, string> { { "memory", "128Mi" } });

    private static readonly List<EnvVarInput> Environments = new() { new EnvVarInput("KEY", "value") };

    private static ScheduleCreateJob CreateSchedule(string? timeZone) => new(
        "0 8 * * 1-5",
        new List<string> { "arg" },
        "image:1.0",
        2,
        120,
        "OnFailure",
        Resources,
        Environments,
        new List<string> { "dependency" },
        timeZone);

    private static UtcScheduleCreateJob CreateUtcSchedule() => new(
        "0 8 * * 1-5",
        new List<string> { "arg" },
        "image:1.0",
        2,
        120,
        "OnFailure",
        Resources,
        Environments,
        new List<string> { "dependency" });

    [Theory(DisplayName = "Sans fuseau horaire, les octets et l'identifiant restent ceux d'avant le fuseau horaire")]
    [InlineData(null)]
    [InlineData("")]
    public void Serialize_Without_TimeZone_Should_Keep_Legacy_Representation(string? timeZone)
    {
        var expected = MemoryPackSerializer.Serialize(CreateUtcSchedule());

        var bytes = ScheduleCreateJobSerializer.Serialize(CreateSchedule(timeZone));

        Assert.Equal(expected, bytes);
        Assert.Equal(9, bytes[0]);
        Assert.Equal(IdGenerator.GetId32Hex(expected), ScheduleCreateJobSerializer.GetId(CreateSchedule(timeZone)));
    }

    [Fact(DisplayName = "Avec un fuseau horaire, le fuseau horaire est sérialisé")]
    public void Serialize_With_TimeZone_Should_Include_TimeZone()
    {
        var schedule = CreateSchedule("Europe/Paris");

        var bytes = ScheduleCreateJobSerializer.Serialize(schedule);

        Assert.Equal(MemoryPackSerializer.Serialize(schedule), bytes);
        Assert.Equal(10, bytes[0]);
        Assert.Equal("Europe/Paris", MemoryPackSerializer.Deserialize<ScheduleCreateJob>(bytes)?.TimeZone);
        Assert.NotEqual(ScheduleCreateJobSerializer.GetId(CreateSchedule(null)), ScheduleCreateJobSerializer.GetId(schedule));
    }

    [Fact(DisplayName = "Les octets sans fuseau horaire se relisent comme une planification UTC")]
    public void Serialize_Without_TimeZone_Should_Deserialize_As_ScheduleCreateJob()
    {
        var bytes = ScheduleCreateJobSerializer.Serialize(CreateSchedule(null));

        var schedule = MemoryPackSerializer.Deserialize<ScheduleCreateJob>(bytes);

        Assert.NotNull(schedule);
        Assert.Equal("0 8 * * 1-5", schedule.Schedule);
        Assert.Equal(new List<string> { "arg" }, schedule.Args);
        Assert.Equal("image:1.0", schedule.Image);
        Assert.Equal(2, schedule.BackoffLimit);
        Assert.Equal(120, schedule.TtlSecondsAfterFinished);
        Assert.Equal("OnFailure", schedule.RestartPolicy);
        Assert.Equal("100m", schedule.Resources?.Requests["cpu"]);
        Assert.Equal("value", Assert.Single(schedule.Environments!).Value);
        Assert.Equal(new List<string> { "dependency" }, schedule.DependsOn);
        Assert.Null(schedule.TimeZone);
    }
}
