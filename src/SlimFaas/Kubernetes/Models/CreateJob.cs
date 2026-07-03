using System.Text.Json.Serialization;
using MemoryPack;

namespace SlimFaas.Kubernetes;

[MemoryPackable]
public partial record CreateJob(
    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    List<string>? DependsOn = null);

[MemoryPackable]
public partial record ScheduleCreateJob(
    string Schedule,
    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    List<string>? DependsOn = null);

[MemoryPackable]
public partial record SlimFaasJobConfiguration(Dictionary<string, SlimfaasJob> Configurations, Dictionary<string, IList<ScheduleCreateJob>>? Schedules=null);

[MemoryPackable]
public partial record SlimfaasJob(
    string Image,
    List<string> ImagesWhitelist,
    CreateJobResources? Resources = null,
    List<string>? DependsOn = null,
    IList<EnvVarInput>? Environments = null,
    int BackoffLimit = 1,
    string Visibility = nameof(FunctionVisibility.Private),
    int NumberParallelJob = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never");

[JsonSerializable(typeof(SlimFaasJobConfiguration))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SlimfaasJobConfigurationSerializerContext : JsonSerializerContext;

[JsonSerializable(typeof(CreateJob))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class CreateJobSerializerContext : JsonSerializerContext;

[JsonSerializable(typeof(ScheduleCreateJob))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ScheduleCreateJobSerializerContext : JsonSerializerContext;

[JsonSerializable(typeof(List<ScheduleCreateJob>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ScheduleCreateJobListSerializerContext : JsonSerializerContext;
