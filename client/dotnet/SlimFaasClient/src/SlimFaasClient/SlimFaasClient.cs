using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SlimFaasClient;

/// <summary>
/// Exception levée quand SlimFaas refuse l'enregistrement du client.
/// </summary>
public class SlimFaasRegistrationException : Exception
{
    public SlimFaasRegistrationException(string message) : base(message) { }
}

/// <summary>
/// Options de connexion du client SlimFaas.
/// </summary>
public class SlimFaasClientOptions
{
    /// <summary>Délai en secondes entre deux tentatives de reconnexion (défaut : 5 s).</summary>
    public double ReconnectDelay { get; set; } = 5.0;

    /// <summary>Intervalle de keepalive ping en secondes (défaut : 30 s, 0 pour désactiver).</summary>
    public double PingInterval { get; set; } = 30.0;

    /// <summary>Taille du buffer de lecture WebSocket en octets (défaut : 64 Ko).</summary>
    public int ReceiveBufferSize { get; set; } = 64 * 1024;
}

/// <summary>
/// Client WebSocket SlimFaas.
///
/// Conceptuellement, chaque instance représente un "replica virtuel" d'une
/// fonction ou d'un job. SlimFaas lui achemine les requêtes asynchrones
/// et les évènements publish/subscribe au lieu de faire des appels HTTP.
/// </summary>
/// <remarks>
/// Exemple d'utilisation :
/// <code>
/// var config = new SlimFaasClientConfig
/// {
///     FunctionName = "my-job",
///     SubscribeEvents = ["order-created"],
/// };
/// await using var client = new SlimFaasClient(
///     new Uri("ws://slimfaas:5003/ws"), config);
///
/// client.OnAsyncRequest = async req =>
/// {
///     Console.WriteLine($"Request: {req.Method} {req.Path}");
///     return 200;
/// };
/// client.OnPublishEvent = async evt =>
///     Console.WriteLine($"Event: {evt.EventName}");
///
/// await client.RunForeverAsync(CancellationToken.None);
/// </code>
///
/// <para>
/// <b>Règles importantes :</b>
/// <list type="bullet">
///   <item>Le <c>FunctionName</c> ne doit pas être le nom d'une fonction Kubernetes existante.</item>
///   <item>Toutes les instances avec le même <c>FunctionName</c> doivent avoir la même configuration.</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SlimFaasClient : IAsyncDisposable
{
    private readonly Uri _uri;
    private readonly SlimFaasClientConfig _config;
    private readonly SlimFaasClientOptions _options;
    private readonly ILogger<SlimFaasClient> _logger;

    private ClientWebSocket? _ws;
    private string? _connectionId;
    private CancellationTokenSource? _cts;

    // ---------------------------------------------------------------------------
    // Callbacks
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Callback appelé pour chaque requête asynchrone reçue.
    /// Doit retourner le code HTTP à renvoyer à SlimFaas (200, 500, etc.).
    /// Retourner 202 indique un traitement long :
    /// appelez alors <see cref="SendCallbackAsync"/> manuellement.
    /// </summary>
    public Func<SlimFaasAsyncRequest, Task<int>>? OnAsyncRequest { get; set; }

    /// <summary>
    /// Callback appelé pour chaque évènement publish/subscribe reçu.
    /// </summary>
    public Func<SlimFaasPublishEvent, Task>? OnPublishEvent { get; set; }

    /// <summary>
    /// Callback appelé pour chaque requête synchrone streamée reçue.
    /// Le callback reçoit la requête qui contient un <see cref="SlimFaasSyncRequest.Response"/>
    /// (<see cref="SyncResponseWriter"/>) pour construire la réponse :
    /// <code>
    /// client.OnSyncRequest = async req =>
    /// {
    ///     await req.Response.StartAsync(200, new() { ["Content-Type"] = ["text/plain"] });
    ///     await req.Response.WriteAsync(Encoding.UTF8.GetBytes("Hello"));
    ///     await req.Response.CompleteAsync();
    /// };
    /// </code>
    /// </summary>
    public Func<SlimFaasSyncRequest, Task>? OnSyncRequest { get; set; }

    // Pending sync request body channels (correlationId → channel de chunks entrants)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Channel<byte[]>> _pendingSyncRequestBodies = new();

    // ---------------------------------------------------------------------------
    // Constructeur
    // ---------------------------------------------------------------------------

    public SlimFaasClient(
        Uri uri,
        SlimFaasClientConfig config,
        SlimFaasClientOptions? options = null,
        ILogger<SlimFaasClient>? logger = null)
    {
        _uri = uri;
        _config = config;
        _options = options ?? new SlimFaasClientOptions();
        _logger = logger ?? NullLogger<SlimFaasClient>.Instance;
    }

    // ---------------------------------------------------------------------------
    // Propriétés
    // ---------------------------------------------------------------------------

    /// <summary>Identifiant de connexion assigné par SlimFaas après enregistrement.</summary>
    public string? ConnectionId => _connectionId;

    /// <summary>True si le WebSocket est actuellement connecté et enregistré.</summary>
    public bool IsConnected => _ws?.State == WebSocketState.Open && _connectionId != null;

    // ---------------------------------------------------------------------------
    // API publique
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Lance la boucle de connexion/reconnexion.
    /// Revient quand <paramref name="ct"/> est annulé.
    /// </summary>
    public async Task RunForeverAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndLoopAsync(ct);
            }
            catch (SlimFaasRegistrationException ex)
            {
                _logger.LogError("SlimFaas registration failed (fatal): {Error}", ex.Message);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "WebSocket disconnected ({Error}). Reconnecting in {Delay:F1} s…",
                    ex.Message,
                    _options.ReconnectDelay);
                await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelay), ct);
            }
        }
    }

    /// <summary>
    /// Envoie manuellement le résultat d'une requête asynchrone.
    /// À utiliser quand <see cref="OnAsyncRequest"/> a retourné 202.
    /// </summary>
    public async Task SendCallbackAsync(string elementId, int statusCode, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        var callback = new AsyncCallbackDto { ElementId = elementId, StatusCode = statusCode };
        var envelope = new SlimFaasEnvelope
        {
            Type = SlimFaasMessageType.AsyncCallback,
            CorrelationId = elementId,
            Payload = JsonSerializer.SerializeToElement(callback, SlimFaasClientJsonContext.Default.AsyncCallbackDto),
        };

        await SendEnvelopeAsync(_ws, envelope, ct);
    }

    // ---------------------------------------------------------------------------
    // IAsyncDisposable
    // ---------------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws != null)
        {
            if (_ws.State == WebSocketState.Open)
            {
                try
                {
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None);
                }
                catch { /* ignore */ }
            }
            _ws.Dispose();
        }
        _cts?.Dispose();
    }

    // ---------------------------------------------------------------------------
    // Implémentation interne
    // ---------------------------------------------------------------------------

    private async Task ConnectAndLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Connecting to SlimFaas WebSocket at {Uri} …", _uri);
        _connectionId = null;

        using var ws = new ClientWebSocket();
        _ws = ws;

        await ws.ConnectAsync(_uri, ct);
        _logger.LogInformation("Connected. Registering function '{FunctionName}' …", _config.FunctionName);

        await RegisterAsync(ws, ct);

        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = _options.PingInterval > 0
            ? PingLoopAsync(ws, pingCts.Token)
            : Task.CompletedTask;

        try
        {
            await ReceiveLoopAsync(ws, ct);
        }
        finally
        {
            await pingCts.CancelAsync();
            await pingTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _ws = null;
            _connectionId = null;
        }
    }

    private async Task RegisterAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        var payloadDto = new RegisterPayloadDto
        {
            FunctionName = _config.FunctionName,
            Configuration = new RegisterConfigDto
            {
                DependsOn = _config.DependsOn,
                SubscribeEvents = _config.SubscribeEvents
                    .Select(e => new SubscribeEventConfigDto
                    {
                        Name = e.Name,
                        Visibility = e.Visibility?.ToString(),
                    })
                    .ToList(),
                DefaultVisibility = _config.DefaultVisibility.ToString(),
                PathsStartWithVisibility = _config.PathsStartWithVisibility
                    .Select(p => new PathVisibilityConfigDto
                    {
                        Path = p.Path,
                        Visibility = p.Visibility.ToString(),
                    })
                    .ToList(),
                Configuration = _config.Configuration,
                ReplicasStartAsSoonAsOneFunctionRetrieveARequest = _config.ReplicasStartAsSoonAsOneFunctionRetrieveARequest,
                NumberParallelRequest = _config.NumberParallelRequest,
                NumberParallelRequestPerPod = _config.NumberParallelRequestPerPod,
                DefaultTrust = _config.DefaultTrust.ToString(),
            },
        };

        var envelope = new SlimFaasEnvelope
        {
            Type = SlimFaasMessageType.Register,
            CorrelationId = correlationId,
            Payload = JsonSerializer.SerializeToElement(payloadDto, SlimFaasClientJsonContext.Default.RegisterPayloadDto),
        };

        await SendEnvelopeAsync(ws, envelope, ct);

        // Attend la réponse d'enregistrement
        var buffer = new byte[_options.ReceiveBufferSize];
        while (!ct.IsCancellationRequested)
        {
            var message = await ReceiveFullMessageAsync(ws, buffer, ct);
            if (message == null) continue;

            var response = JsonSerializer.Deserialize(message, SlimFaasClientJsonContext.Default.SlimFaasEnvelope);
            if (response?.Type != SlimFaasMessageType.RegisterResponse) continue;

            if (!response.Payload.HasValue)
            {
                throw new SlimFaasRegistrationException("Empty RegisterResponse payload.");
            }

            var resp = response.Payload.Value.Deserialize(SlimFaasClientJsonContext.Default.RegisterResponseDto);
            if (resp == null || !resp.Success)
            {
                throw new SlimFaasRegistrationException(resp?.Error ?? "Unknown registration error.");
            }

            _connectionId = resp.ConnectionId;
            _logger.LogInformation("Registered successfully. connectionId={ConnectionId}", _connectionId);
            return;
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[_options.ReceiveBufferSize];

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // ── Frame binaire → streaming synchrone ──
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                HandleBinaryFrame(ws, ms.ToArray(), ct);
                continue;
            }

            // ── Frame texte → message JSON classique ──
            var message = Encoding.UTF8.GetString(ms.ToArray());

            SlimFaasEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(message, SlimFaasClientJsonContext.Default.SlimFaasEnvelope);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize WebSocket message");
                continue;
            }

            if (envelope == null) continue;

            await HandleEnvelopeAsync(ws, envelope, ct);
        }
    }

    /// <summary>
    /// Traite une frame binaire de streaming synchrone (SyncRequestStart, SyncRequestChunk, SyncRequestEnd, SyncCancel).
    /// </summary>
    private void HandleBinaryFrame(ClientWebSocket ws, byte[] data, CancellationToken ct)
    {
        if (data.Length < BinaryFrame.HeaderSize)
        {
            _logger.LogWarning("Received binary frame too short ({Length} bytes)", data.Length);
            return;
        }

        var (type, correlationId, flags, payloadLength) = BinaryFrame.DecodeHeader(data);
        var payload = data.AsSpan(BinaryFrame.HeaderSize, Math.Min(payloadLength, data.Length - BinaryFrame.HeaderSize));

        switch (type)
        {
            case SlimFaasMessageType.SyncRequestStart:
            {
                SyncRequestStartDto? startDto;
                try
                {
                    startDto = JsonSerializer.Deserialize(payload, SlimFaasClientJsonContext.Default.SyncRequestStartDto);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize SyncRequestStart");
                    return;
                }
                if (startDto == null) return;

                var bodyChannel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
                _pendingSyncRequestBodies[correlationId] = bodyChannel;

                var syncReq = new SlimFaasSyncRequest
                {
                    CorrelationId = correlationId,
                    Method = startDto.Method,
                    Path = startDto.Path,
                    Query = startDto.Query,
                    Headers = startDto.Headers,
                    Body = new ChannelStream(bodyChannel.Reader),
                    Response = new SyncResponseWriter(
                        correlationId,
                        SendSyncResponseStartAsync,
                        SendSyncResponseChunkAsync,
                        SendSyncResponseEndAsync),
                };

                // Dispatch dans une tâche séparée
                _ = Task.Run(() => DispatchSyncRequestAsync(ws, syncReq, ct), ct);
                break;
            }

            case SlimFaasMessageType.SyncRequestChunk:
            {
                if (_pendingSyncRequestBodies.TryGetValue(correlationId, out var ch))
                    ch.Writer.TryWrite(payload.ToArray());
                break;
            }

            case SlimFaasMessageType.SyncRequestEnd:
            {
                if (_pendingSyncRequestBodies.TryGetValue(correlationId, out var ch))
                {
                    ch.Writer.TryComplete();
                    _pendingSyncRequestBodies.TryRemove(correlationId, out _);
                }
                break;
            }

            case SlimFaasMessageType.SyncCancel:
            {
                if (_pendingSyncRequestBodies.TryRemove(correlationId, out var ch))
                    ch.Writer.TryComplete(new OperationCanceledException("Stream cancelled by server"));
                break;
            }

            default:
                _logger.LogDebug("Unexpected binary frame type: {Type}", type);
                break;
        }
    }

    private async Task HandleEnvelopeAsync(ClientWebSocket ws, SlimFaasEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case SlimFaasMessageType.AsyncRequest:
                if (!envelope.Payload.HasValue) break;
                var reqDto = envelope.Payload.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncRequestDto);
                if (reqDto == null) break;
                var req = MapRequest(reqDto);
                // Dispatch dans une tâche séparée pour ne pas bloquer la lecture
                _ = Task.Run(() => DispatchAsyncRequestAsync(ws, req, ct), ct);
                break;

            case SlimFaasMessageType.PublishEvent:
                if (!envelope.Payload.HasValue) break;
                var evtDto = envelope.Payload.Value.Deserialize(SlimFaasClientJsonContext.Default.PublishEventDto);
                if (evtDto == null) break;
                var evt = MapEvent(evtDto);
                _ = Task.Run(() => DispatchPublishEventAsync(evt, ct), ct);
                break;

            case SlimFaasMessageType.Pong:
                _logger.LogDebug("Pong received");
                break;

            default:
                _logger.LogDebug("Unhandled message type: {Type}", envelope.Type);
                break;
        }
    }

    private async Task DispatchAsyncRequestAsync(ClientWebSocket ws, SlimFaasAsyncRequest req, CancellationToken ct)
    {
        if (OnAsyncRequest == null)
        {
            _logger.LogWarning(
                "Received AsyncRequest for {ElementId} but no handler registered. Returning 500.",
                req.ElementId);
            await SendCallbackInternalAsync(ws, req.ElementId, 500, ct);
            return;
        }

        int statusCode;
        try
        {
            statusCode = await OnAsyncRequest(req);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AsyncRequest handler threw an exception for {ElementId}", req.ElementId);
            statusCode = 500;
        }

        // 202 = le client gérera le callback lui-même via SendCallbackAsync
        if (statusCode != 202)
        {
            await SendCallbackInternalAsync(ws, req.ElementId, statusCode, ct);
        }
    }

    private async Task DispatchPublishEventAsync(SlimFaasPublishEvent evt, CancellationToken ct)
    {
        if (OnPublishEvent == null)
        {
            _logger.LogDebug("Received PublishEvent '{EventName}' but no handler registered.", evt.EventName);
            return;
        }

        try
        {
            await OnPublishEvent(evt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PublishEvent handler threw an exception for event '{EventName}'", evt.EventName);
        }
    }

    private async Task DispatchSyncRequestAsync(ClientWebSocket ws, SlimFaasSyncRequest req, CancellationToken ct)
    {
        if (OnSyncRequest == null)
        {
            _logger.LogWarning("Received SyncRequest for {CorrelationId} but no handler registered. Returning 500.", req.CorrelationId);
            await req.Response.StartAsync(500, ct: ct);
            await req.Response.CompleteAsync(ct);
            return;
        }

        try
        {
            await OnSyncRequest(req);
            // Auto-complete if the handler forgot to call CompleteAsync
            await req.Response.CompleteAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncRequest handler threw an exception for {CorrelationId}", req.CorrelationId);
            try
            {
                await req.Response.StartAsync(500, ct: ct);
                await req.Response.CompleteAsync(ct);
            }
            catch { /* ignore — response may have been partially sent */ }
        }
    }

    /// <summary>
    /// Envoie le début de la réponse synchrone (status + headers) pour un stream donné.
    /// </summary>
    public async Task SendSyncResponseStartAsync(string correlationId, SlimFaasSyncResponse response, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var dto = new SyncResponseStartDto
        {
            StatusCode = response.StatusCode,
            Headers = response.Headers,
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, SlimFaasClientJsonContext.Default.SyncResponseStartDto);
        var frame = BinaryFrame.Encode(SlimFaasMessageType.SyncResponseStart, correlationId, json);
        await SendBinaryAsync(_ws, frame, ct);
    }

    /// <summary>
    /// Envoie un chunk du body de la réponse synchrone.
    /// </summary>
    public async Task SendSyncResponseChunkAsync(string correlationId, ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var frame = BinaryFrame.Encode(SlimFaasMessageType.SyncResponseChunk, correlationId, chunk.Span);
        await SendBinaryAsync(_ws, frame, ct);
    }

    /// <summary>
    /// Signale la fin du body de la réponse synchrone.
    /// </summary>
    public async Task SendSyncResponseEndAsync(string correlationId, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            throw new InvalidOperationException("WebSocket is not connected.");

        var frame = BinaryFrame.Encode(SlimFaasMessageType.SyncResponseEnd, correlationId, BinaryFrame.FlagEndOfStream);
        await SendBinaryAsync(_ws, frame, ct);
    }

    /// <summary>
    /// Annule un stream synchrone en cours.
    /// </summary>
    public async Task SendSyncCancelAsync(string correlationId, CancellationToken ct = default)
    {
        if (_ws == null || _ws.State != WebSocketState.Open) return;

        var frame = BinaryFrame.Encode(SlimFaasMessageType.SyncCancel, correlationId);
        await SendBinaryAsync(_ws, frame, ct);
    }

    private async Task SendCallbackInternalAsync(ClientWebSocket ws, string elementId, int statusCode, CancellationToken ct)
    {
        var callback = new AsyncCallbackDto { ElementId = elementId, StatusCode = statusCode };
        var envelope = new SlimFaasEnvelope
        {
            Type = SlimFaasMessageType.AsyncCallback,
            CorrelationId = elementId,
            Payload = JsonSerializer.SerializeToElement(callback, SlimFaasClientJsonContext.Default.AsyncCallbackDto),
        };
        await SendEnvelopeAsync(ws, envelope, ct);
    }

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(_options.PingInterval), ct);
            try
            {
                var ping = new SlimFaasEnvelope
                {
                    Type = SlimFaasMessageType.Ping,
                    CorrelationId = Guid.NewGuid().ToString("N"),
                };
                await SendEnvelopeAsync(ws, ping, ct);
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private async Task SendEnvelopeAsync(ClientWebSocket ws, SlimFaasEnvelope envelope, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(envelope, SlimFaasClientJsonContext.Default.SlimFaasEnvelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendBinaryAsync(ClientWebSocket ws, byte[] frame, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(
                new ArraySegment<byte>(frame),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveFullMessageAsync(
        ClientWebSocket ws,
        byte[] buffer,
        CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // ---------------------------------------------------------------------------
    // Mapping DTOs -> modèles publics
    // ---------------------------------------------------------------------------

    private static SlimFaasAsyncRequest MapRequest(AsyncRequestDto dto) =>
        new()
        {
            ElementId = dto.ElementId,
            Method = dto.Method,
            Path = dto.Path,
            Query = dto.Query,
            Headers = dto.Headers,
            Body = dto.Body != null ? Convert.FromBase64String(dto.Body) : null,
            IsLastTry = dto.IsLastTry,
            TryNumber = dto.TryNumber,
        };

    private static SlimFaasPublishEvent MapEvent(PublishEventDto dto) =>
        new()
        {
            EventName = dto.EventName,
            Method = dto.Method,
            Path = dto.Path,
            Query = dto.Query,
            Headers = dto.Headers,
            Body = dto.Body != null ? Convert.FromBase64String(dto.Body) : null,
        };
}

