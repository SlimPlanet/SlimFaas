using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;

internal static class ScheduleJobSerialization
{
    internal static byte[] Serialize(ScheduleCreateJob schedule)
    {
        if (!string.IsNullOrEmpty(schedule.TimeZone))
        {
            return MemoryPackSerializer.Serialize(schedule);
        }

        // Keep old readers and static schedule watermarks compatible when no time zone is used.
        return MemoryPackSerializer.Serialize(new LegacyScheduleCreateJobPayload(
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

    internal static string GetId(ScheduleCreateJob schedule) => IdGenerator.GetId32Hex(Serialize(schedule));
}

// This member order is the persisted format from before TimeZone was introduced.
// Keep it unchanged: static schedule IDs are hashes of these exact bytes.
[MemoryPackable]
internal sealed partial record LegacyScheduleCreateJobPayload(
    string Schedule,
    List<string> Args,
    string Image,
    int BackoffLimit,
    int TtlSecondsAfterFinished,
    string RestartPolicy,
    CreateJobResources? Resources,
    IList<EnvVarInput>? Environments,
    List<string>? DependsOn);
