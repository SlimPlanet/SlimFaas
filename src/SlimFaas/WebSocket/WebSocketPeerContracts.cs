using System.Text.Json.Serialization;

namespace SlimFaas.WebSocket;

/// <summary>Authenticated context for a single owner-only delivery.</summary>
internal sealed record WebSocketPeerContext(
    string Sender, string Owner, string Incarnation, string Operation, string Function,
    string Fingerprint, string Nonce, long DeadlineTicks, string PayloadHash,
    string? ActivitySourcePod, string? ActivityCorrelationId);

/// <summary>Async or event delivery to an explicitly selected set of owner-local connections.</summary>
internal sealed record WebSocketPeerRequest(
    CustomRequest Request, string? ElementId, int TryNumber, bool IsLastTry,
    string? EventName, string[] ConnectionIds);

/// <summary>Client callback status, distinct from the internal HTTP transport status.</summary>
internal sealed record WebSocketPeerResult(int StatusCode);

[JsonSerializable(typeof(WebSocketPeerContext))]
[JsonSerializable(typeof(WebSocketPeerRequest))]
[JsonSerializable(typeof(WebSocketPeerResult))]
internal sealed partial class WebSocketPeerJsonContext : JsonSerializerContext;
