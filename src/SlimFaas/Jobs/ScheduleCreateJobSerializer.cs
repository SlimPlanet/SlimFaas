using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;

/// <summary>
/// MemoryPack bytes and identifier of a <see cref="ScheduleCreateJob"/>.
/// A schedule without <see cref="ScheduleCreateJob.TimeZone"/> keeps the representation written before
/// the time zone was added, so its static identifier, its execution tracking and the nodes that do not
/// know the time zone yet are unaffected.
/// </summary>
public static class ScheduleCreateJobSerializer
{
    public static byte[] Serialize(ScheduleCreateJob schedule)
    {
        if (!string.IsNullOrEmpty(schedule.TimeZone))
        {
            return MemoryPackSerializer.Serialize(schedule);
        }

        return MemoryPackSerializer.Serialize(new UtcScheduleCreateJob(
            schedule.Schedule,
            schedule.Args,
            schedule.Image,
            schedule.BackoffLimit,
            schedule.TtlSecondsAfterFinished,
            schedule.RestartPolicy,
            schedule.Resources,
            schedule.Environments,
            schedule.DependsOn));
    }

    public static string GetId(ScheduleCreateJob schedule)
        => IdGenerator.GetId32Hex(Serialize(schedule));
}

/// <summary>
/// <see cref="ScheduleCreateJob"/> members written before <see cref="ScheduleCreateJob.TimeZone"/>.
/// Members must stay identical to the leading members of <see cref="ScheduleCreateJob"/>.
/// </summary>
[MemoryPackable]
internal partial record UtcScheduleCreateJob(
    string Schedule,
    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    List<string>? DependsOn = null);
