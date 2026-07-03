using System.Text.Json.Serialization;

namespace SlimFaas.Kubernetes;

public record SlimFaasDeploymentInformation(int Replicas, IList<PodInformation> Pods);

public record DeploymentsInformations(
    IList<DeploymentInformation> Functions,
    SlimFaasDeploymentInformation SlimFaas,
    IEnumerable<PodInformation> Pods);

[JsonSerializable(typeof(DeploymentsInformations))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class DeploymentsInformationsSerializerContext : JsonSerializerContext;

public record ResourcesConfiguration(
    string? CpuRequest = null,
    string? CpuLimit = null,
    string? MemoryRequest = null,
    string? MemoryLimit = null);

public record DeploymentInformation(
    string Deployment,
    string Namespace,
    IList<PodInformation> Pods,
    SlimFaasConfiguration Configuration,
    int Replicas,
    int ReplicasAtStart = 1,
    int ReplicasMin = 0,
    int TimeoutSecondBeforeSetReplicasMin = 300,
    int NumberParallelRequest = 10,
    bool ReplicasStartAsSoonAsOneFunctionRetrieveARequest = false,
    PodType PodType = PodType.Deployment,
    IList<string>? DependsOn = null,
    ScheduleConfig? Schedule = null,
    IList<SubscribeEvent>? SubscribeEvents = null,
    FunctionVisibility Visibility = FunctionVisibility.Public,
    IList<PathVisibility>? PathsStartWithVisibility = null,
    string ResourceVersion = "",
    bool EndpointReady = false,
    FunctionTrust Trust = FunctionTrust.Trusted,
    ScaleConfig? Scale = null,
    int NumberParallelRequestPerPod = 10,
    ResourcesConfiguration? Resources = null
);

public record PodInformation(
    string Name,
    bool? Started,
    bool? Ready,
    string Ip,
    string DeploymentName,
    IList<int>? Ports = null,
    string ResourceVersion = "",
    string? ServiceName = null)
{
    public IDictionary<string, string>? Annotations { get; init; }
    public string? StartFailureReason { get; init; }
    public string? StartFailureMessage { get; init; }
    public string? AppFailureReason { get; init;}
    public string? AppFailureMessage { get; init; }
}
