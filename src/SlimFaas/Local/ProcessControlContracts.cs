using System.Text.Json.Serialization;
using SlimFaas.Kubernetes;

namespace SlimFaas.Local;

public sealed record ProcessScaleCommand(int Replicas);

public sealed record ProcessCreateJobCommand(
    string Namespace,
    string Name,
    CreateJob CreateJob,
    string ElementId,
    string JobFullName,
    long InQueueTimestamp);

public sealed record ProcessControlError(string Error);

public sealed record LocalStateMetadata(
    int SchemaVersion,
    string Project,
    int Nodes,
    int EntrypointPort,
    int NodeHttpPortBase,
    int RaftPortBase,
    int ProcessPortFrom,
    int ProcessPortTo,
    Dictionary<string, int> PortAllocations);

[JsonSerializable(typeof(ProcessScaleCommand))]
[JsonSerializable(typeof(ProcessCreateJobCommand))]
[JsonSerializable(typeof(ProcessControlError))]
[JsonSerializable(typeof(DeploymentsInformations))]
[JsonSerializable(typeof(SlimFaasJobConfiguration))]
[JsonSerializable(typeof(List<Job>))]
[JsonSerializable(typeof(LocalStateMetadata))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ProcessControlJsonContext : JsonSerializerContext;
