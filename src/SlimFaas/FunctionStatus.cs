using System.Text.Json.Serialization;

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

