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

    // ── Streaming synchrone (frames binaires) ────────────────────────
    SyncRequestStart = 0x10,
    SyncRequestChunk = 0x11,
    SyncRequestEnd = 0x12,
    SyncResponseStart = 0x20,
    SyncResponseChunk = 0x21,
    SyncResponseEnd = 0x22,
    SyncCancel = 0x30,
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
// Structures pour SubscribeEvents et PathsStartWithVisibility
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Enums typés (remplacent les magic strings)
// ---------------------------------------------------------------------------

/// <summary>Visibilité d'une fonction, d'un évènement ou d'un path.</summary>
public enum FunctionVisibility
{
    /// <summary>Accessible depuis l'extérieur du namespace.</summary>
    Public,
    /// <summary>Accessible uniquement depuis l'intérieur du namespace.</summary>
    Private,
}

/// <summary>Niveau de confiance d'une fonction.</summary>
public enum FunctionTrust
{
    /// <summary>Fonction de confiance (pas de restrictions supplémentaires).</summary>
    Trusted,
    /// <summary>Fonction non-fiable (restrictions de sécurité appliquées).</summary>
    Untrusted,
}

// ---------------------------------------------------------------------------
// Structures pour SubscribeEvents et PathsStartWithVisibility
// ---------------------------------------------------------------------------

/// <summary>
/// Décrit un évènement auquel s'abonner, avec une visibilité optionnelle.
/// Si <see cref="Visibility"/> est null, la visibilité par défaut (<c>DefaultVisibility</c>) est utilisée.
/// Exemple : <c>new SubscribeEventConfig { Name = "fibo-public", Visibility = FunctionVisibility.Public }</c>
/// </summary>
public class SubscribeEventConfig
{
    /// <summary>Nom de l'évènement (ex : "fibo-public").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Surcharge de visibilité, ou null pour hériter de <c>DefaultVisibility</c>.
    /// </summary>
    public FunctionVisibility? Visibility { get; set; }
}

/// <summary>
/// Décrit une règle de visibilité par préfixe de chemin.
/// Exemple : <c>new PathVisibilityConfig { Path = "/admin", Visibility = FunctionVisibility.Private }</c>
/// </summary>
public class PathVisibilityConfig
{
    /// <summary>Préfixe de chemin (ex : "/admin").</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Visibilité de ce préfixe de chemin.</summary>
    public FunctionVisibility Visibility { get; set; } = FunctionVisibility.Public;
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

    /// <summary>
    /// SlimFaas/SubscribeEvents.
    /// Chaque entrée peut surcharger la visibilité individuellement.
    /// Si <see cref="SubscribeEventConfig.Visibility"/> est null, <see cref="DefaultVisibility"/> est utilisé.
    /// </summary>
    public List<SubscribeEventConfig> SubscribeEvents { get; set; } = [];

    /// <summary>SlimFaas/DefaultVisibility</summary>
    public FunctionVisibility DefaultVisibility { get; set; } = FunctionVisibility.Public;

    /// <summary>
    /// SlimFaas/PathsStartWithVisibility.
    /// Chaque entrée définit un préfixe de chemin et sa visibilité.
    /// </summary>
    public List<PathVisibilityConfig> PathsStartWithVisibility { get; set; } = [];

    /// <summary>SlimFaas/Configuration</summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>SlimFaas/ReplicasStartAsSoonAsOneFunctionRetrieveARequest</summary>
    public bool ReplicasStartAsSoonAsOneFunctionRetrieveARequest { get; set; }

    /// <summary>SlimFaas/NumberParallelRequest</summary>
    public int NumberParallelRequest { get; set; } = 10;

    /// <summary>SlimFaas/NumberParallelRequestPerPod</summary>
    public int NumberParallelRequestPerPod { get; set; } = 10;

    /// <summary>SlimFaas/DefaultTrust</summary>
    public FunctionTrust DefaultTrust { get; set; } = FunctionTrust.Trusted;
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
    public List<SubscribeEventConfigDto> SubscribeEvents { get; set; } = [];

    [JsonPropertyName("defaultVisibility")]
    public string DefaultVisibility { get; set; } = "Public";

    [JsonPropertyName("pathsStartWithVisibility")]
    public List<PathVisibilityConfigDto> PathsStartWithVisibility { get; set; } = [];

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

internal class SubscribeEventConfigDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }
}

internal class PathVisibilityConfigDto
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("visibility")]
    public string Visibility { get; set; } = "Public";
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
/// </summary>
public static class BinaryFrame
{
    /// <summary>Taille du header fixe d'une frame binaire.</summary>
    public const int HeaderSize = 42;

    /// <summary>Flag : fin du stream.</summary>
    public const byte FlagEndOfStream = 0x01;

    /// <summary>Encode une frame binaire.</summary>
    public static byte[] Encode(SlimFaasMessageType type, string correlationId, ReadOnlySpan<byte> payload, byte flags = 0)
    {
        var frame = new byte[HeaderSize + payload.Length];
        frame[0] = (byte)type;
        var corrBytes = System.Text.Encoding.ASCII.GetBytes(correlationId.PadRight(36)[..36]);
        Buffer.BlockCopy(corrBytes, 0, frame, 1, 36);
        frame[37] = flags;
        frame[38] = (byte)(payload.Length >> 24);
        frame[39] = (byte)(payload.Length >> 16);
        frame[40] = (byte)(payload.Length >> 8);
        frame[41] = (byte)(payload.Length);
        if (payload.Length > 0) payload.CopyTo(frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>Encode une frame sans payload.</summary>
    public static byte[] Encode(SlimFaasMessageType type, string correlationId, byte flags = 0)
        => Encode(type, correlationId, ReadOnlySpan<byte>.Empty, flags);

    /// <summary>Décode le header d'une frame binaire.</summary>
    public static (SlimFaasMessageType Type, string CorrelationId, byte Flags, int PayloadLength) DecodeHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
            throw new ArgumentException($"Binary frame header must be at least {HeaderSize} bytes.");
        var type = (SlimFaasMessageType)header[0];
        var correlationId = System.Text.Encoding.ASCII.GetString(header.Slice(1, 36)).Trim();
        byte flags = header[37];
        int length = (header[38] << 24) | (header[39] << 16) | (header[40] << 8) | header[41];
        return (type, correlationId, flags, length);
    }
}

/// <summary>
/// Stream en lecture seule alimenté au fil de l'eau depuis un <see cref="Channel{T}"/>.
/// Expose les chunks binaires reçus via WebSocket comme un <see cref="Stream"/> standard.
/// Le stream se termine (retourne 0) quand le channel est complété (fin de body).
/// </summary>
public sealed class ChannelStream : Stream
{
    private readonly System.Threading.Channels.ChannelReader<byte[]> _reader;
    private byte[]? _currentChunk;
    private int _currentOffset;
    private bool _completed;

    internal ChannelStream(System.Threading.Channels.ChannelReader<byte[]> reader)
    {
        _reader = reader;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_completed) return 0;

        // Vider d'abord le chunk courant s'il en reste
        while (_currentChunk == null || _currentOffset >= _currentChunk.Length)
        {
            // Attendre le prochain chunk depuis le channel
            if (!await _reader.WaitToReadAsync(cancellationToken))
            {
                // Channel complété → fin du stream
                _completed = true;
                return 0;
            }
            if (!_reader.TryRead(out _currentChunk))
            {
                _currentChunk = null;
                continue;
            }
            _currentOffset = 0;
        }

        // Copier ce qu'on peut dans le buffer demandé
        int available = _currentChunk.Length - _currentOffset;
        int toCopy = Math.Min(available, buffer.Length);
        _currentChunk.AsSpan(_currentOffset, toCopy).CopyTo(buffer.Span);
        _currentOffset += toCopy;

        return toCopy;
    }
}

/// <summary>Requête synchrone streamée reçue par le client WebSocket.</summary>
public class SlimFaasSyncRequest
{
    /// <summary>Identifiant de corrélation du stream.</summary>
    public string CorrelationId { get; set; } = string.Empty;
    public string Method { get; set; } = "GET";
    public string Path { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public Dictionary<string, string[]> Headers { get; set; } = [];

    /// <summary>
    /// Stream en lecture seule du body de la requête HTTP, alimenté au fil de l'eau.
    /// Lit les chunks binaires tels qu'ils arrivent via WebSocket.
    /// Se termine (Read retourne 0) quand tout le body a été reçu.
    /// </summary>
    public Stream Body { get; set; } = Stream.Null;

    /// <summary>
    /// Writer pour construire la réponse synchrone.
    /// Utilisation :
    /// <code>
    /// await req.Response.StartAsync(200, new() { ["Content-Type"] = ["application/json"] });
    /// await req.Response.WriteAsync(bytes, 0, bytes.Length);
    /// await req.Response.CompleteAsync();
    /// </code>
    /// </summary>
    public SyncResponseWriter Response { get; init; } = null!;
}

/// <summary>
/// Stream en écriture seule qui envoie la réponse synchrone vers SlimFaas.
/// Encapsule l'envoi de SyncResponseStart, SyncResponseChunk et SyncResponseEnd
/// pour une corrélation donnée.
/// </summary>
public sealed class SyncResponseWriter : Stream
{
    private readonly Func<string, SlimFaasSyncResponse, CancellationToken, Task> _sendStart;
    private readonly Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> _sendChunk;
    private readonly Func<string, CancellationToken, Task> _sendEnd;
    private readonly string _correlationId;
    private bool _started;
    private bool _completed;

    internal SyncResponseWriter(
        string correlationId,
        Func<string, SlimFaasSyncResponse, CancellationToken, Task> sendStart,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> sendChunk,
        Func<string, CancellationToken, Task> sendEnd)
    {
        _correlationId = correlationId;
        _sendStart = sendStart;
        _sendChunk = sendChunk;
        _sendEnd = sendEnd;
    }

    /// <summary>
    /// Envoie le début de la réponse (status code + headers).
    /// Doit être appelé une seule fois, avant tout <see cref="WriteAsync(byte[], int, int, CancellationToken)"/>.
    /// </summary>
    public async Task StartAsync(int statusCode = 200, Dictionary<string, string[]>? headers = null, CancellationToken ct = default)
    {
        if (_started) throw new InvalidOperationException("Response already started.");
        if (_completed) throw new InvalidOperationException("Response already completed.");
        _started = true;
        await _sendStart(_correlationId, new SlimFaasSyncResponse
        {
            StatusCode = statusCode,
            Headers = headers ?? [],
        }, ct);
    }

    /// <summary>
    /// Signale la fin de la réponse. Aucun chunk ne peut être envoyé après cet appel.
    /// Si <see cref="StartAsync"/> n'a pas été appelé, un status 200 est envoyé automatiquement.
    /// </summary>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        if (_completed) return;
        if (!_started) await StartAsync(ct: ct);
        _completed = true;
        await _sendEnd(_correlationId, ct);
    }

    // ── Stream overrides ─────────────────────────────────────────────────

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_completed) throw new InvalidOperationException("Response already completed.");
        if (!_started) await StartAsync(ct: ct);
        if (count > 0)
            await _sendChunk(_correlationId, buffer.AsMemory(offset, count), ct);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_completed) throw new InvalidOperationException("Response already completed.");
        if (!_started) await StartAsync(ct: ct);
        if (buffer.Length > 0)
            await _sendChunk(_correlationId, buffer, ct);
    }
}

/// <summary>Réponse synchrone streamée envoyée par le client WebSocket.</summary>
public class SlimFaasSyncResponse
{
    public int StatusCode { get; set; } = 200;
    public Dictionary<string, string[]> Headers { get; set; } = [];
}

internal class SyncRequestStartDto
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

internal class SyncResponseStartDto
{
    [JsonPropertyName("statusCode")]
    public int StatusCode { get; set; } = 200;
    [JsonPropertyName("headers")]
    public Dictionary<string, string[]> Headers { get; set; } = [];
}

// ---------------------------------------------------------------------------
// Source-generated JSON context
// ---------------------------------------------------------------------------

[JsonSerializable(typeof(SlimFaasEnvelope))]
[JsonSerializable(typeof(RegisterPayloadDto))]
[JsonSerializable(typeof(RegisterConfigDto))]
[JsonSerializable(typeof(SubscribeEventConfigDto))]
[JsonSerializable(typeof(PathVisibilityConfigDto))]
[JsonSerializable(typeof(List<SubscribeEventConfigDto>))]
[JsonSerializable(typeof(List<PathVisibilityConfigDto>))]
[JsonSerializable(typeof(RegisterResponseDto))]
[JsonSerializable(typeof(AsyncRequestDto))]
[JsonSerializable(typeof(PublishEventDto))]
[JsonSerializable(typeof(AsyncCallbackDto))]
[JsonSerializable(typeof(SyncRequestStartDto))]
[JsonSerializable(typeof(SyncResponseStartDto))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class SlimFaasClientJsonContext : JsonSerializerContext;

