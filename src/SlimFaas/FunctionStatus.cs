using System.Text.Json.Serialization;
using SlimFaas.Kubernetes;

namespace SlimFaas;

/// <summary>
/// Représente le statut d'une fonction SlimFaas
/// </summary>
/// <param name="NumberReady">Nombre de pods prêts</param>
/// <param name="NumberRequested">Nombre de replicas demandés</param>
/// <param name="PodType">Type de pod (Deployment, Job, etc.)</param>
/// <param name="Visibility">Visibilité de la fonction (Public, Private)</param>
/// <param name="FunctionName">Nom de la fonction</param>
public record FunctionStatus(
    int NumberReady,
    int NumberRequested,
    string PodType,
    string Visibility,
    string Name);

public record PodStatus(
    string Name,
    string Status,
    bool Ready,
    string Ip);

public record FunctionStatusDetailed(
    string Name,
    int NumberReady,
    int NumberRequested,
    string PodType,
    string Visibility,
    int ReplicasMin,
    int ReplicasAtStart,
    int TimeoutSecondBeforeSetReplicasMin,
    int NumberParallelRequest,
    int NumberParallelRequestPerPod,
    ResourcesConfiguration? Resources,
    ScheduleConfig? Schedule,
    IList<SubscribeEvent>? SubscribeEvents,
    IList<PathVisibility>? PathsStartWithVisibility,
    IList<string>? DependsOn,
    IList<PodStatus> Pods);

/// <summary>
/// Contexte de sérialisation JSON pour FunctionStatus (compatible AOT)
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FunctionStatus))]
[JsonSerializable(typeof(List<FunctionStatus>))]
public partial class FunctionStatusSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Contexte de sérialisation JSON pour List FunctionStatus (compatible AOT)
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<FunctionStatus>))]
public partial class ListFunctionStatusSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Contexte de sérialisation JSON pour FunctionStatusDetailed (compatible AOT)
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(FunctionStatusDetailed))]
[JsonSerializable(typeof(List<FunctionStatusDetailed>))]
public partial class FunctionStatusDetailedSerializerContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<FunctionStatusDetailed>))]
public partial class ListFunctionStatusDetailedSerializerContext : JsonSerializerContext
{
}

