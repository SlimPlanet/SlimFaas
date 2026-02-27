using System.Text.Json.Serialization;

namespace SlimFaasClient;

// ---------------------------------------------------------------------------
// Protocole WebSocket (doit correspondre à WebSocketMessageType côté serveur)
// ---------------------------------------------------------------------------

public enum SlimFaasMessageType
{
    Register = 0,
    RegisterResponse = 1,
    AsyncRequest = 2,
    AsyncCallback = 3,
    PublishEvent = 4,
    Ping = 5,
    Pong = 6,
}

// ---------------------------------------------------------------------------
// Enveloppe générique
// ---------------------------------------------------------------------------

public class SlimFaasEnvelope
{
    [JsonPropertyName("type")]
    public SlimFaasMessageType Type { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("payload")]
    public System.Text.Json.JsonElement? Payload { get; set; }
}

// ---------------------------------------------------------------------------
// Configuration de la fonction/job
// ---------------------------------------------------------------------------

/// <summary>
/// Configuration d'une fonction ou d'un job WebSocket.
///
/// Correspond aux annotations Kubernetes SlimFaas :
/// <list type="bullet">
///   <item>SlimFaas/DependsOn</item>
///   <item>SlimFaas/SubscribeEvents</item>
///   <item>SlimFaas/DefaultVisibility</item>
///   <item>SlimFaas/PathsStartWithVisibility</item>
///   <item>SlimFaas/Configuration</item>
///   <item>SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest</item>
///   <item>SlimFaas/NumberParallelRequest</item>
///   <item>SlimFaas/NumberParallelRequestPerPod</item>
///   <item>SlimFaas/DefaultTrust</item>
/// </list>
/// </summary>
public class SlimFaasClientConfig
{
    /// <summary>
    /// Nom unique de la fonction ou du job (équivalent au nom du Deployment Kubernetes).
    /// </summary>
    public string FunctionName { get; set; } = string.Empty;

    /// <summary>SlimFaas/DependsOn</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>SlimFaas/SubscribeEvents</summary>
    public List<string> SubscribeEvents { get; set; } = [];

    /// <summary>SlimFaas/DefaultVisibility : "Public" ou "Private"</summary>
    public string DefaultVisibility { get; set; } = "Public";

    /// <summary>SlimFaas/PathsStartWithVisibility</summary>
    public Dictionary<string, string> PathsStartWithVisibility { get; set; } = [];

    /// <summary>SlimFaas/Configuration</summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest</summary>
    public bool ReplicasStartAsSoonAsOneFunctionRetrieveARequest { get; set; }

    /// <summary>SlimFaas/NumberParallelRequest</summary>
    public int NumberParallelRequest { get; set; } = 10;

    /// <summary>SlimFaas/NumberParallelRequestPerPod</summary>
    public int NumberParallelRequestPerPod { get; set; } = 10;

    /// <summary>SlimFaas/DefaultTrust : "Trusted" ou "Untrusted"</summary>
    public string DefaultTrust { get; set; } = "Trusted";
}

// ---------------------------------------------------------------------------
// Messages reçus par le client
// ---------------------------------------------------------------------------

/// <summary>Requête asynchrone envoyée par SlimFaas au client WebSocket.</summary>
public class SlimFaasAsyncRequest
{
    public string ElementId { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public Dictionary<string, string[]> Headers { get; set; } = [];
    /// <summary>Corps de la requête (décodé depuis base64).</summary>
    public byte[]? Body { get; set; }
    public bool IsLastTry { get; set; }
    public int TryNumber { get; set; } = 1;
}

/// <summary>Évènement publish/subscribe reçu depuis SlimFaas.</summary>
public class SlimFaasPublishEvent
{
    public string EventName { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string Path { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public Dictionary<string, string[]> Headers { get; set; } = [];
    /// <summary>Corps de l'évènement (décodé depuis base64).</summary>
    public byte[]? Body { get; set; }
}

// ---------------------------------------------------------------------------
// Payloads internes (sérialisation JSON)
// ---------------------------------------------------------------------------

internal class RegisterPayloadDto
{
    [JsonPropertyName("functionName")]
    public string FunctionName { get; set; } = string.Empty;

    [JsonPropertyName("configuration")]
    public RegisterConfigDto Configuration { get; set; } = new();
}

internal class RegisterConfigDto
{
    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = [];

    [JsonPropertyName("subscribeEvents")]
    public List<string> SubscribeEvents { get; set; } = [];

    [JsonPropertyName("defaultVisibility")]
    public string DefaultVisibility { get; set; } = "Public";

    [JsonPropertyName("pathsStartWithVisibility")]
    public Dictionary<string, string> PathsStartWithVisibility { get; set; } = [];

    [JsonPropertyName("configuration")]
    public string Configuration { get; set; } = string.Empty;

    [JsonPropertyName("replicasStartAsSoonAsOneFunctionRetrieveARequest")]
    public bool ReplicasStartAsSoonAsOneFunctionRetrieveARequest { get; set; }

    [JsonPropertyName("numberParallelRequest")]
    public int NumberParallelRequest { get; set; } = 10;

    [JsonPropertyName("numberParallelRequestPerPod")]
    public int NumberParallelRequestPerPod { get; set; } = 10;

    [JsonPropertyName("defaultTrust")]
    public string DefaultTrust { get; set; } = "Trusted";
}

internal class RegisterResponseDto
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;
}

internal class AsyncRequestDto
{
    [JsonPropertyName("elementId")]
    public string ElementId { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "POST";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string[]> Headers { get; set; } = [];

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("isLastTry")]
    public bool IsLastTry { get; set; }

    [JsonPropertyName("tryNumber")]
    public int TryNumber { get; set; } = 1;
}

internal class PublishEventDto
{
    [JsonPropertyName("eventName")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("method")]
    public string Method { get; set; } = "POST";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string[]> Headers { get; set; } = [];

    [JsonPropertyName("body")]
    public string? Body { get; set; }
}

internal class AsyncCallbackDto
{
    [JsonPropertyName("elementId")]
    public string ElementId { get; set; } = string.Empty;

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; } = 200;
}

// ---------------------------------------------------------------------------
// Source-generated JSON context
// ---------------------------------------------------------------------------

[JsonSerializable(typeof(SlimFaasEnvelope))]
[JsonSerializable(typeof(RegisterPayloadDto))]
[JsonSerializable(typeof(RegisterConfigDto))]
[JsonSerializable(typeof(RegisterResponseDto))]
[JsonSerializable(typeof(AsyncRequestDto))]
[JsonSerializable(typeof(PublishEventDto))]
[JsonSerializable(typeof(AsyncCallbackDto))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SlimFaasClientJsonContext : JsonSerializerContext;

