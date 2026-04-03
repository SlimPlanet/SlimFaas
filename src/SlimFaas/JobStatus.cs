using System.Text.Json.Serialization;
using SlimFaas.Kubernetes;

namespace SlimFaas;

public record RunningJobStatus(
    string Name,
    string Status,
    string ElementId,
    long InQueueTimestamp,
    long StartTimestamp);

public record ScheduledJobInfo(
    string Id,
    string Schedule,
    string Image,
    long? NextExecutionTimestamp,
    CreateJobResources? Resources,
    List<string>? DependsOn);

public record JobConfigurationStatus(
    string Name,
    string Visibility,
    string Image,
    IList<string> ImagesWhitelist,
    int NumberParallelJob,
    CreateJobResources? Resources,
    List<string>? DependsOn,
    IList<ScheduledJobInfo> Schedules,
    IList<RunningJobStatus> RunningJobs);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<JobConfigurationStatus>))]
public partial class ListJobConfigurationStatusSerializerContext : JsonSerializerContext
{
}

