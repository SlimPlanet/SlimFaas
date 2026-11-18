using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimData;
using SlimFaas.Streams;

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
        using var connection = new WebSocketClientConnection { Socket = socket, Ready = false };
        connection.Streams = context.RequestServices.GetService<IStreamService>();
        connection.RequestContext = context;

        try
        {
            await ReceiveLoopAsync(socket, connection, registry, replicasService, logger, context.RequestAborted);
        }
        finally
        {
            connection.Ready = false;
            if (!string.IsNullOrEmpty(connection.FunctionName))
            {
                registry.Unregister(connection);
                if (context.RequestServices.GetService<IWebSocketClusterDirectory>() is { } directory)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await directory.RemoveAsync(connection, cleanup.Token); }
                    catch (Exception exception)
                    {
                        logger.LogWebSocketDirectoryLifecycleFailed(exception, connection.FunctionName);
                    }
                }
            }

            // Résoudre tous les callbacks en attente avec une erreur
            foreach (var (_, tcs) in connection.PendingCallbacks)
            {
                tcs.TrySetResult(503);
            }

            // Annuler tous les sync streams en cours
            foreach (var (_, stream) in connection.PendingSyncStreams)
            {
                await stream.CancelAsync();
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
                await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            }
            while (!result.EndOfMessage);

            // ── Frame binaire → streaming synchrone ──
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary)
            {
                await HandleBinaryFrameAsync(ms.ToArray(), connection, logger);
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
                logger.LogFailedToDeserializeWebSocketMessage(ex);
                continue;
            }

            if (envelope == null) continue;

            await HandleMessageAsync(envelope, connection, registry, replicasService, logger, ct);
        }
    }

    /// <summary>
    /// Traite une frame binaire de streaming synchrone (SyncResponseStart, SyncResponseChunk, SyncResponseEnd, SyncCancel).
    /// </summary>
    private static async Task HandleBinaryFrameAsync(
        byte[] data,
        WebSocketClientConnection connection,
        ILogger logger)
    {
        if (data.Length < BinaryFrame.HeaderSize)
        {
            logger.LogReceivedBinaryFrameTooShortBytes(data.Length);
            return;
        }

        var (type, correlationId, flags, payloadLength) = BinaryFrame.DecodeHeader(data);
        var payload = data.AsSpan(BinaryFrame.HeaderSize, Math.Min(payloadLength, data.Length - BinaryFrame.HeaderSize));

        if (!connection.PendingSyncStreams.TryGetValue(correlationId, out var pendingStream))
        {
            logger.LogReceivedBinaryFrameForUnknownCorrelationId(correlationId, type);
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
                        logger.LogSyncResponseStartReceivedCorrelationIdStatus(correlationId, responseStart.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogFailedToDeserializeSyncResponseStartPayload(ex);
                    pendingStream.ResponseStartTcs.TrySetException(ex);
                }
                break;

            case WebSocketMessageType.SyncResponseChunk:
                try { await pendingStream.ResponseChunks.Writer.WriteAsync(payload.ToArray(), pendingStream.Token); }
                catch (Exception exception) when (pendingStream.Token.IsCancellationRequested &&
                    (exception is OperationCanceledException or ChannelClosedException))
                { logger.LogSyncFrameDiscardedAfterCancellation(correlationId); }
                break;

            case WebSocketMessageType.SyncResponseEnd:
                pendingStream.ResponseChunks.Writer.TryComplete();
                pendingStream.ResponseEndTcs.TrySetResult();
                logger.LogSyncResponseEndReceivedCorrelationId(correlationId);
                break;

            case WebSocketMessageType.SyncCancel:
                await pendingStream.CancelAsync();
                pendingStream.ResponseChunks.Writer.TryComplete(new OperationCanceledException());
                pendingStream.ResponseStartTcs.TrySetCanceled();
                pendingStream.ResponseEndTcs.TrySetCanceled();
                connection.PendingSyncStreams.TryRemove(correlationId, out _);
                logger.LogSyncCancelReceivedCorrelationId(correlationId);
                break;

            default:
                logger.LogUnexpectedBinaryFrameType(type);
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
                await HandleAsyncCallback(envelope, connection, logger);
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
                logger.LogUnhandledWebSocketMessageType(envelope.Type);
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
            logger.LogFailedToDeserializeRegisterPayload(ex);
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.FunctionName) || connection.FunctionName.Length > 0)
        {
            await connection.SendAsync(new WebSocketEnvelope
            {
                Type = WebSocketMessageType.RegisterResponse,
                CorrelationId = envelope.CorrelationId,
                Payload = JsonSerializer.SerializeToElement(
                    new RegisterResponsePayload { Success = false, Error = connection.FunctionName.Length > 0
                        ? "This connection is already registered." : "Invalid register payload: functionName is required." },
                    AppJsonContext.Default.RegisterResponsePayload),
            }, ct);
            return;
        }

        connection.FunctionName = payload.FunctionName;
        connection.Configuration = payload.Configuration ?? new WebSocketFunctionConfiguration();

        var (success, error) = registry.TryRegister(connection, replicasService);
        var directory = connection.RequestContext?.RequestServices.GetService<IWebSocketClusterDirectory>();
        try
        {
            if (success && directory is not null)
            {
                var reserved = await directory.ReserveAsync(connection, ct);
                success = reserved.Status < 400;
                error = reserved.Error;
            }
            if (success && connection.Streams is { } streams)
            {
                var function = WebSocketFunctionRepository.CreateDeployment(payload.FunctionName, connection.Configuration,
                    [connection.ConnectionId]);
                error = await StreamConfiguration.ApplyAsync(streams, function, connection.RequestContext, ct,
                    StreamConfiguration.Fingerprint(connection.Configuration));
                success = error is null;
            }
            else if (success && connection.Configuration.SubscribeStreams.Count > 0)
            {
                success = false;
                error = "Durable Stream delivery is unavailable.";
            }
            if (success && directory is not null)
            {
                connection.Ready = true;
                var activated = await directory.ActivateAsync(connection, ct);
                success = activated.Status < 400;
                error = activated.Error;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWebSocketDirectoryLifecycleFailed(exception, payload.FunctionName);
            success = false;
            error = "Cluster function registration is unavailable.";
        }
        if (!success)
        {
            connection.Ready = false;
            registry.Unregister(connection);
            if (directory is not null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await directory.RemoveAsync(connection, cleanup.Token); }
                catch (Exception exception)
                {
                    logger.LogWebSocketDirectoryLifecycleFailed(exception, payload.FunctionName);
                }
            }
            connection.FunctionName = "";
        }
        connection.Ready = success;

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
            logger.LogWebSocketRegistrationRefusedFor(payload.FunctionName, error);
        }
    }

    private static async Task HandleAsyncCallback(
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
            logger.LogFailedToDeserializeAsyncCallbackPayload(ex);
            return;
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.ElementId))
        {
            logger.LogAsyncCallbackReceivedWithMissingElementId();
            return;
        }

        if (payload.ElementId.StartsWith("stream_", StringComparison.Ordinal))
        {
            if (connection.Streams is not { } streams || connection.RequestContext is not { } context)
            {
                logger.LogAsyncCallbackForUnknownElementId(payload.ElementId);
                return;
            }
            var state = await streams.ReadAsync(context.RequestAborted);
            var attempt = DurableStreamStore.Attempt(state, payload.ElementId);
            var definition = attempt is null ? null : DurableStreamStore.Definition(state, attempt.Stream);
            if (attempt is null || definition is null || attempt.Function != connection.FunctionName || !streams.CanAccess(context, definition))
            {
                logger.LogAsyncCallbackForUnknownElementId(payload.ElementId);
                return;
            }
            await streams.MutateAsync(new()
            {
                Action = DurableStreamAction.Complete, Stream = attempt.Stream, Function = attempt.Function,
                ElementId = payload.ElementId, StatusCode = payload.StatusCode
            }, context.RequestAborted);
            if (payload.StatusCode != 202 && connection.StreamRequests.TryRemove(payload.ElementId, out var reserved) && reserved == 1)
                connection.ReleaseRequest();
            return;
        }
        if (connection.PendingCallbacks.TryRemove(payload.ElementId, out var tcs))
        {
            if (tcs.TrySetResult(payload.StatusCode))
                logger.LogAsyncCallbackResolvedElementIdStatus(payload.ElementId, payload.StatusCode);
            else
                logger.LogAsyncCallbackForUnknownElementId(payload.ElementId);
        }
        else
        {
            logger.LogAsyncCallbackForUnknownElementId(payload.ElementId);
        }
    }
}
