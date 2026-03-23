using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.WebSocket;

/// <summary>
/// Endpoint WebSocket exposé par SlimFaas sur un port dédié.
/// Les clients (jobs, fonctions virtuelles) s'y connectent et reçoivent
/// leurs requêtes asynchrones et leurs évènements via ce canal.
///
/// URL de connexion : ws://&lt;slimfaas&gt;:&lt;wsPort&gt;/ws
/// </summary>
public static class WebSocketEndpoints
{
    public static void MapWebSocketEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map("/ws", HandleWebSocket);
    }

    private static async Task HandleWebSocket(
        HttpContext context,
        WebSocketConnectionRegistry registry,
        IReplicasService replicasService,
        IOptions<SlimFaasOptions> slimFaasOptions,
        ILogger<WebSocketConnectionRegistry> logger)
    {
        // Vérifier que la requête arrive bien sur le port WebSocket dédié
        int wsPort = slimFaasOptions.Value.WebSocketPort;
        if (wsPort > 0)
        {
            int localPort = context.Connection.LocalPort;
            int hostPort = context.Request.Host.Port ?? 0;
            if (localPort != wsPort && hostPort != wsPort)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Expected a WebSocket request.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new WebSocketClientConnection { Socket = socket };

        try
        {
            await ReceiveLoopAsync(socket, connection, registry, replicasService, logger, context.RequestAborted);
        }
        finally
        {
            if (connection.FunctionName != string.Empty)
            {
                registry.Unregister(connection);
            }

            // Résoudre tous les callbacks en attente avec une erreur
            foreach (var (_, tcs) in connection.PendingCallbacks)
            {
                tcs.TrySetResult(503);
            }

            // Annuler tous les sync streams en cours
            foreach (var (_, stream) in connection.PendingSyncStreams)
            {
                stream.Cts.Cancel();
                stream.ResponseChunks.Writer.TryComplete(new OperationCanceledException("WebSocket disconnected"));
                stream.ResponseStartTcs.TrySetCanceled();
                stream.ResponseEndTcs.TrySetCanceled();
            }
            connection.PendingSyncStreams.Clear();
        }
    }

    private static async Task ReceiveLoopAsync(
        System.Net.WebSockets.WebSocket socket,
        WebSocketClientConnection connection,
        WebSocketConnectionRegistry registry,
        IReplicasService replicasService,
        ILogger logger,
        CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            using var ms = new MemoryStream();

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", ct);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            // ── Frame binaire → streaming synchrone ──
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary)
            {
                HandleBinaryFrame(ms.ToArray(), connection, logger);
                continue;
            }

            // ── Frame texte → message JSON classique ──
            string json = Encoding.UTF8.GetString(ms.ToArray());

            WebSocketEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(json, AppJsonContext.Default.WebSocketEnvelope);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deserialize WebSocket message");
                continue;
            }

            if (envelope == null) continue;

            await HandleMessageAsync(envelope, connection, registry, replicasService, logger, ct);
        }
    }

    /// <summary>
    /// Traite une frame binaire de streaming synchrone (SyncResponseStart, SyncResponseChunk, SyncResponseEnd, SyncCancel).
    /// </summary>
    private static void HandleBinaryFrame(
        byte[] data,
        WebSocketClientConnection connection,
        ILogger logger)
    {
        if (data.Length < BinaryFrame.HeaderSize)
        {
            logger.LogWarning("Received binary frame too short ({Length} bytes)", data.Length);
            return;
        }

        var (type, correlationId, flags, payloadLength) = BinaryFrame.DecodeHeader(data);
        var payload = data.AsSpan(BinaryFrame.HeaderSize, Math.Min(payloadLength, data.Length - BinaryFrame.HeaderSize));

        if (!connection.PendingSyncStreams.TryGetValue(correlationId, out var pendingStream))
        {
            logger.LogWarning("Received binary frame for unknown correlationId={CorrelationId} type={Type}", correlationId, type);
            return;
        }

        switch (type)
        {
            case WebSocketMessageType.SyncResponseStart:
                try
                {
                    var responseStart = JsonSerializer.Deserialize(payload, AppJsonContext.Default.SyncResponseStartPayload);
                    if (responseStart != null)
                    {
                        pendingStream.ResponseStartTcs.TrySetResult(responseStart);
                        logger.LogDebug("SyncResponseStart received: correlationId={CorrelationId} status={StatusCode}",
                            correlationId, responseStart.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to deserialize SyncResponseStart payload");
                    pendingStream.ResponseStartTcs.TrySetException(ex);
                }
                break;

            case WebSocketMessageType.SyncResponseChunk:
                pendingStream.ResponseChunks.Writer.TryWrite(payload.ToArray());
                break;

            case WebSocketMessageType.SyncResponseEnd:
                pendingStream.ResponseChunks.Writer.TryComplete();
                pendingStream.ResponseEndTcs.TrySetResult();
                logger.LogDebug("SyncResponseEnd received: correlationId={CorrelationId}", correlationId);
                break;

            case WebSocketMessageType.SyncCancel:
                pendingStream.Cts.Cancel();
                pendingStream.ResponseChunks.Writer.TryComplete(new OperationCanceledException());
                pendingStream.ResponseStartTcs.TrySetCanceled();
                pendingStream.ResponseEndTcs.TrySetCanceled();
                connection.PendingSyncStreams.TryRemove(correlationId, out _);
                logger.LogDebug("SyncCancel received: correlationId={CorrelationId}", correlationId);
                break;

            default:
                logger.LogDebug("Unexpected binary frame type: {Type}", type);
                break;
        }
    }

    private static async Task HandleMessageAsync(
        WebSocketEnvelope envelope,
        WebSocketClientConnection connection,
        WebSocketConnectionRegistry registry,
        IReplicasService replicasService,
        ILogger logger,
        CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case WebSocketMessageType.Register:
                await HandleRegisterAsync(envelope, connection, registry, replicasService, logger, ct);
                break;

            case WebSocketMessageType.AsyncCallback:
                HandleAsyncCallback(envelope, connection, logger);
                break;

            case WebSocketMessageType.Ping:
                await connection.SendAsync(new WebSocketEnvelope
                {
                    Type = WebSocketMessageType.Pong,
                    CorrelationId = envelope.CorrelationId,
                    Payload = null,
                }, ct);
                break;

            default:
                logger.LogDebug("Unhandled WebSocket message type: {Type}", envelope.Type);
                break;
        }
    }

    private static async Task HandleRegisterAsync(
        WebSocketEnvelope envelope,
        WebSocketClientConnection connection,
        WebSocketConnectionRegistry registry,
        IReplicasService replicasService,
        ILogger logger,
        CancellationToken ct)
    {
        RegisterPayload? payload = null;
        try
        {
            if (envelope.Payload.HasValue)
            {
                payload = envelope.Payload.Value.Deserialize(AppJsonContext.Default.RegisterPayload);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize Register payload");
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.FunctionName))
        {
            await connection.SendAsync(new WebSocketEnvelope
            {
                Type = WebSocketMessageType.RegisterResponse,
                CorrelationId = envelope.CorrelationId,
                Payload = JsonSerializer.SerializeToElement(
                    new RegisterResponsePayload { Success = false, Error = "Invalid register payload: functionName is required." },
                    AppJsonContext.Default.RegisterResponsePayload),
            }, ct);
            return;
        }

        connection.FunctionName = payload.FunctionName;
        connection.Configuration = payload.Configuration ?? new WebSocketFunctionConfiguration();

        var (success, error) = registry.TryRegister(connection, replicasService);

        await connection.SendAsync(new WebSocketEnvelope
        {
            Type = WebSocketMessageType.RegisterResponse,
            CorrelationId = envelope.CorrelationId,
            Payload = JsonSerializer.SerializeToElement(
                new RegisterResponsePayload
                {
                    Success = success,
                    Error = error,
                    ConnectionId = success ? connection.ConnectionId : string.Empty,
                },
                AppJsonContext.Default.RegisterResponsePayload),
        }, ct);

        if (!success)
        {
            logger.LogWarning("WebSocket registration refused for '{FunctionName}': {Error}",
                payload.FunctionName, error);
        }
    }

    private static void HandleAsyncCallback(
        WebSocketEnvelope envelope,
        WebSocketClientConnection connection,
        ILogger logger)
    {
        AsyncCallbackPayload? payload = null;
        try
        {
            if (envelope.Payload.HasValue)
            {
                payload = envelope.Payload.Value.Deserialize(AppJsonContext.Default.AsyncCallbackPayload);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to deserialize AsyncCallback payload");
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.ElementId))
        {
            logger.LogWarning("AsyncCallback received with missing elementId");
            return;
        }

        if (connection.PendingCallbacks.TryRemove(payload.ElementId, out var tcs))
        {
            tcs.TrySetResult(payload.StatusCode);
            logger.LogDebug("AsyncCallback resolved: elementId={ElementId} status={Status}",
                payload.ElementId, payload.StatusCode);
        }
        else
        {
            logger.LogWarning("AsyncCallback for unknown elementId={ElementId}", payload.ElementId);
        }
    }
}

