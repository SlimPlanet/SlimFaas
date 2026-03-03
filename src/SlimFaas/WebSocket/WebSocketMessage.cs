using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimFaas.WebSocket;

/// <summary>
/// Types de messages échangés via WebSocket entre SlimFaas et les clients.
/// </summary>
public enum WebSocketMessageType
{
    /// <summary>Enregistrement du client auprès de SlimFaas.</summary>
    Register = 0,

    /// <summary>Réponse d'enregistrement (ACK ou erreur).</summary>
    RegisterResponse = 1,

    /// <summary>Requête asynchrone à exécuter par le client (async-function).</summary>
    AsyncRequest = 2,

    /// <summary>Callback de résultat envoyé par le client après traitement.</summary>
    AsyncCallback = 3,

    /// <summary>Évènement publish/subscribe à transmettre au client.</summary>
    PublishEvent = 4,

    /// <summary>Ping de keepalive.</summary>
    Ping = 5,

    /// <summary>Pong de keepalive.</summary>
    Pong = 6,
}

/// <summary>
/// Message générique échangé via WebSocket.
/// Le payload est encodé en JSON brut (JsonElement) pour éviter les problèmes AOT.
/// </summary>
public class WebSocketEnvelope
{
    [JsonPropertyName("type")]
    public WebSocketMessageType Type { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Payload encodé en JSON brut.</summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// Payload envoyé par le client lors de son enregistrement.
/// </summary>
public class RegisterPayload
{
    [JsonPropertyName("functionName")]
    public string FunctionName { get; set; } = string.Empty;

    [JsonPropertyName("configuration")]
    public WebSocketFunctionConfiguration Configuration { get; set; } = new();
}

/// <summary>
/// Décrit un évènement auquel s'abonner, avec une visibilité optionnelle.
/// Si <see cref="Visibility"/> est null, la visibilité par défaut (<c>DefaultVisibility</c>) est utilisée.
/// </summary>
public class SubscribeEventConfig
{
    /// <summary>Nom de l'évènement (ex : "fibo-public").</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Surcharge de visibilité : "Public", "Private" ou null pour hériter de DefaultVisibility.</summary>
    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }
}

/// <summary>
/// Décrit une règle de visibilité par préfixe de chemin.
/// </summary>
public class PathVisibilityConfig
{
    /// <summary>Préfixe de chemin (ex : "/admin").</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Visibilité : "Public" ou "Private".</summary>
    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "Public";
}

/// <summary>
/// Configuration de la fonction/job déclarée par le client WebSocket.
/// Correspond aux annotations SlimFaas habituelles.
/// </summary>
public class WebSocketFunctionConfiguration
{
    [JsonPropertyName("dependsOn")]
    public List<string> DependsOn { get; set; } = [];

    /// <summary>
    /// Évènements auxquels s'abonner.
    /// Chaque entrée peut surcharger la visibilité individuellement.
    /// </summary>
    [JsonPropertyName("subscribeEvents")]
    public List<SubscribeEventConfig> SubscribeEvents { get; set; } = [];

    [JsonPropertyName("defaultVisibility")]
    public string DefaultVisibility { get; set; } = "Public";

    /// <summary>
    /// Règles de visibilité par préfixe de chemin.
    /// </summary>
    [JsonPropertyName("pathsStartWithVisibility")]
    public List<PathVisibilityConfig> PathsStartWithVisibility { get; set; } = [];

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

/// <summary>
/// Réponse du serveur à un enregistrement.
/// </summary>
public class RegisterResponsePayload
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = string.Empty;
}

/// <summary>
/// Payload d'une requête asynchrone transmise au client WebSocket (au lieu d'un appel HTTP).
/// </summary>
public class AsyncRequestPayload
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
    public int TryNumber { get; set; }
}

/// <summary>
/// Payload du callback envoyé par le client après traitement d'une requête asynchrone.
/// </summary>
public class AsyncCallbackPayload
{
    [JsonPropertyName("elementId")]
    public string ElementId { get; set; } = string.Empty;

    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; } = 200;
}

/// <summary>
/// Payload d'un évènement publish/subscribe transmis au client WebSocket.
/// </summary>
public class PublishEventPayload
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

