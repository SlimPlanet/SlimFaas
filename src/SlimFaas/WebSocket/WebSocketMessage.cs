using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimFaas.WebSocket;

/// <summary>
/// Types de messages échangés via WebSocket entre SlimFaas et les clients.
/// Valeurs 0-6 : messages JSON textuels (enveloppe).
/// Valeurs 0x10-0x40 : frames binaires pour le streaming synchrone.
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

    // ── Streaming synchrone (frames binaires) ────────────────────────

    /// <summary>Début d'une requête synchrone streamée (SlimFaas → Client). Payload = headers JSON.</summary>
    SyncRequestStart = 0x10,

    /// <summary>Chunk du body de la requête synchrone (SlimFaas → Client). Payload = bytes bruts.</summary>
    SyncRequestChunk = 0x11,

    /// <summary>Fin du body de la requête synchrone (SlimFaas → Client). Pas de payload.</summary>
    SyncRequestEnd = 0x12,

    /// <summary>Début de la réponse synchrone (Client → SlimFaas). Payload = status + headers JSON.</summary>
    SyncResponseStart = 0x20,

    /// <summary>Chunk du body de la réponse synchrone (Client → SlimFaas). Payload = bytes bruts.</summary>
    SyncResponseChunk = 0x21,

    /// <summary>Fin du body de la réponse synchrone (Client → SlimFaas). Pas de payload.</summary>
    SyncResponseEnd = 0x22,

    /// <summary>Annulation d'un stream (bidirectionnel). Pas de payload.</summary>
    SyncCancel = 0x30,
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

// ---------------------------------------------------------------------------
// Streaming synchrone — frames binaires
// ---------------------------------------------------------------------------

/// <summary>
/// Utilitaire pour encoder/décoder des frames binaires de streaming synchrone.
///
/// Format d'une frame binaire (42 octets de header fixe) :
/// <code>
/// ┌─────────────┬──────────────┬────────────┬────────────┬──────────────┐
/// │  type (1B)  │ corrId (36B) │ flags (1B) │ length(4B) │ payload (nB) │
/// └─────────────┴──────────────┴────────────┴────────────┴──────────────┘
/// </code>
///
/// Le correlationId est un GUID en format "D" (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx, 36 chars ASCII).
/// </summary>
public static class BinaryFrame
{
    /// <summary>Taille du header fixe d'une frame binaire.</summary>
    public const int HeaderSize = 42; // 1 + 36 + 1 + 4

    /// <summary>Flag : fin du stream (aucun chunk supplémentaire ne suivra).</summary>
    public const byte FlagEndOfStream = 0x01;

    /// <summary>Encode une frame binaire.</summary>
    public static byte[] Encode(WebSocketMessageType type, string correlationId, ReadOnlySpan<byte> payload, byte flags = 0)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = (byte)type;

        // CorrelationId en ASCII (36 chars, format GUID "D")
        var corrBytes = System.Text.Encoding.ASCII.GetBytes(correlationId.PadRight(36)[..36]);
        Buffer.BlockCopy(corrBytes, 0, frame, 1, 36);

        frame[37] = flags;

        // Longueur du payload en big-endian
        frame[38] = (byte)(payload.Length >> 24);
        frame[39] = (byte)(payload.Length >> 16);
        frame[40] = (byte)(payload.Length >> 8);
        frame[41] = (byte)(payload.Length);

        if (payload.Length > 0)
        {
            payload.CopyTo(frame.AsSpan(HeaderSize));
        }

        return frame;
    }

    /// <summary>Encode une frame sans payload (ex: SyncRequestEnd, SyncResponseEnd, SyncCancel).</summary>
    public static byte[] Encode(WebSocketMessageType type, string correlationId, byte flags = 0)
    {
        return Encode(type, correlationId, ReadOnlySpan<byte>.Empty, flags);
    }

    /// <summary>Décode le header d'une frame binaire.</summary>
    public static (WebSocketMessageType Type, string CorrelationId, byte Flags, int PayloadLength) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            throw new ArgumentException($"Binary frame header must be at least {HeaderSize} bytes.");

        var type = (WebSocketMessageType)header[0];
        var correlationId = System.Text.Encoding.ASCII.GetString(header.Slice(1, 36)).Trim();
        byte flags = header[37];
        int length = (header[38] << 24) | (header[39] << 16) | (header[40] << 8) | header[41];

        return (type, correlationId, flags, length);
    }
}

/// <summary>
/// Payload JSON du message SyncRequestStart (headers de la requête HTTP).
/// Envoyé comme payload de la frame binaire SyncRequestStart.
/// </summary>
public class SyncRequestStartPayload
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "GET";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("headers")]
    public Dictionary<string, string[]> Headers { get; set; } = [];
}

/// <summary>
/// Payload JSON du message SyncResponseStart (status + headers de la réponse HTTP).
/// Envoyé comme payload de la frame binaire SyncResponseStart.
/// </summary>
public class SyncResponseStartPayload
{
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; } = 200;

    [JsonPropertyName("headers")]
    public Dictionary<string, string[]> Headers { get; set; } = [];
}

