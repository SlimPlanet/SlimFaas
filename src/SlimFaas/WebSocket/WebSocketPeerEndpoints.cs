using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using SlimFaas.Endpoints;

namespace SlimFaas.WebSocket;

public static class WebSocketPeerEndpoints
{
    public static void MapWebSocketPeerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/internal/websocket/sync", HandleSyncAsync)
            .WithName("WebSocketPeerSync").ExcludeFromDescription().DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();
        app.MapPost("/internal/websocket/async", HandleAsyncAsync)
            .WithName("WebSocketPeerAsync").ExcludeFromDescription().DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();
        app.MapPost("/internal/websocket/event", HandleEventAsync)
            .WithName("WebSocketPeerEvent").ExcludeFromDescription().DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static CancellationTokenSource Budget(HttpContext context, WebSocketPeerContext proof)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        budget.CancelAfter(TimeSpan.FromTicks(Math.Max(1, proof.DeadlineTicks - DateTime.UtcNow.Ticks)));
        return budget;
    }

    private static async Task<WebSocketPeerRequest> ReadRequestAsync(HttpContext context, WebSocketPeerContext proof,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > 32 * 1024 * 1024)
                throw new InvalidDataException("WebSocket peer request exceeds the size limit.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        var bytes = body.ToArray();
        if (!WebSocketPeerAuthorization.MatchesPayload(proof, bytes))
            throw new InvalidDataException("WebSocket peer payload does not match its proof.");
        return JsonSerializer.Deserialize(bytes, WebSocketPeerJsonContext.Default.WebSocketPeerRequest)
            ?? throw new InvalidDataException("Missing WebSocket peer request.");
    }

    private static async Task<IResult> HandleAsyncAsync(HttpContext context, WebSocketPeerAuthorization authorization,
        WebSocketSendClient local, ILogger<WebSocketPeerAuthorization> logger)
    {
        var proof = authorization.Authenticate(context, "async");
        if (proof is null) return Results.NotFound();
        using var budget = Budget(context, proof);
        try
        {
            var request = await ReadRequestAsync(context, proof, budget.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.ElementId) || request.EventName is not null)
                throw new InvalidDataException("Invalid asynchronous peer delivery.");
            int status = await local.SendAsync(proof.Function, request.Request, request.ElementId,
                request.IsLastTry, request.TryNumber, budget.Token).ConfigureAwait(false);
            return Results.Json(new WebSocketPeerResult(status), WebSocketPeerJsonContext.Default.WebSocketPeerResult);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            logger.LogRelayProofInvalid(exception);
            return Results.BadRequest();
        }
    }

    private static async Task<IResult> HandleEventAsync(HttpContext context, WebSocketPeerAuthorization authorization,
        WebSocketSendClient local, WebSocketConnectionRegistry registry, ILogger<WebSocketPeerAuthorization> logger)
    {
        var proof = authorization.Authenticate(context, "event");
        if (proof is null) return Results.NotFound();
        using var budget = Budget(context, proof);
        try
        {
            var request = await ReadRequestAsync(context, proof, budget.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(request.EventName) || request.ConnectionIds.Length == 0)
                throw new InvalidDataException("Invalid event peer delivery.");
            var configuration = registry.GetConfiguration(proof.Function);
            if (configuration?.SubscribeEvents.Any(subscription => subscription.Name == request.EventName) != true)
                throw new InvalidDataException("This owner is not subscribed to the relayed event.");
            var ids = request.ConnectionIds.ToHashSet(StringComparer.Ordinal);
            bool connected = registry.GetConnections(proof.Function).Any(connection => ids.Contains(connection.ConnectionId));
            await local.PublishEventToConnectionsAsync(proof.Function, request.Request, request.EventName, ids,
                proof.ActivitySourcePod, proof.ActivityCorrelationId, budget.Token).ConfigureAwait(false);
            return Results.Json(new WebSocketPeerResult(connected ? 204 : 503), WebSocketPeerJsonContext.Default.WebSocketPeerResult);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            logger.LogRelayProofInvalid(exception);
            return Results.BadRequest();
        }
    }

    private static async Task<IResult> HandleSyncAsync(HttpContext context, WebSocketPeerAuthorization authorization,
        WebSocketSendClient local, ILogger<WebSocketPeerAuthorization> logger)
    {
        var proof = authorization.Authenticate(context, "sync");
        if (proof is null || !context.WebSockets.IsWebSocketRequest) return Results.NotFound();
        using var budget = Budget(context, proof);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        string correlation = new Guid(proof.Nonce).ToString("D");
        var body = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
        await using var requestBody = new WebSocketPeerBodyStream(body.Reader);
        Task incoming = Task.CompletedTask;
        bool started = false;
        bool completed = false;
        try
        {
            var first = await WebSocketPeerFrames.ReceiveAsync(socket, correlation, budget.Token).ConfigureAwait(false);
            if (first.Type != WebSocketMessageType.SyncRequestStart || !WebSocketPeerAuthorization.MatchesPayload(proof, first.Payload))
                throw new InvalidDataException("Invalid synchronous peer request start.");
            var request = JsonSerializer.Deserialize(first.Payload, AppJsonContext.Default.SyncRequestStartPayload)
                ?? throw new InvalidDataException("Missing synchronous peer headers.");
            incoming = ReadBodyAsync();
            var response = await local.SendSyncRequestStreamAsync(proof.Function, request.Method, request.Path, request.Query,
                WebSocketPeerHeaders.Filter(request.Headers), requestBody, proof.ActivitySourcePod,
                proof.ActivityCorrelationId, budget.Token).ConfigureAwait(false);
            await WebSocketPeerFrames.SendAsync(socket, WebSocketMessageType.SyncResponseStart, correlation,
                JsonSerializer.SerializeToUtf8Bytes(new SyncResponseStartPayload
                {
                    StatusCode = response.StatusCode, Headers = WebSocketPeerHeaders.Filter(response.Headers)
                }, AppJsonContext.Default.SyncResponseStartPayload), budget.Token).ConfigureAwait(false);
            started = true;
            await foreach (var chunk in response.BodyChunks.ReadAllAsync(budget.Token).ConfigureAwait(false))
                await WebSocketPeerFrames.SendAsync(socket, WebSocketMessageType.SyncResponseChunk, correlation, chunk, budget.Token)
                    .ConfigureAwait(false);
            await response.WaitForEnd().ConfigureAwait(false);
            await WebSocketPeerFrames.SendAsync(socket, WebSocketMessageType.SyncResponseEnd, correlation,
                ReadOnlyMemory<byte>.Empty, budget.Token).ConfigureAwait(false);
            completed = true;
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Delivery complete", budget.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or WebSocketException or InvalidOperationException or
            OperationCanceledException or JsonException)
        {
            logger.LogRelayOperationFailed(exception, "sync", proof.Function);
            if (!started && socket.State == WebSocketState.Open)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await WebSocketPeerFrames.SendAsync(socket, WebSocketMessageType.SyncResponseStart, correlation,
                        JsonSerializer.SerializeToUtf8Bytes(new SyncResponseStartPayload
                        {
                            StatusCode = exception is OperationCanceledException ? 504 : 503
                        }, AppJsonContext.Default.SyncResponseStartPayload), cleanup.Token).ConfigureAwait(false);
                    await WebSocketPeerFrames.SendAsync(socket, WebSocketMessageType.SyncResponseEnd, correlation,
                        ReadOnlyMemory<byte>.Empty, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception sending) when (sending is WebSocketException or OperationCanceledException or IOException)
                { logger.LogRelayOperationFailed(sending, "sync error response", proof.Function); }
            }
        }
        finally
        {
            try
            {
                if (!completed)
                {
                    await budget.CancelAsync().ConfigureAwait(false);
                    socket.Abort();
                }
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await incoming.WaitAsync(closing.Token).ConfigureAwait(false);
                    if (completed && socket.State is WebSocketState.Open or WebSocketState.CloseSent or WebSocketState.CloseReceived)
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Delivery complete", closing.Token)
                            .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or WebSocketException or OperationCanceledException)
                {
                    logger.LogRelayOperationFailed(exception, "sync upload/close", proof.Function);
                    socket.Abort();
                }
            }
            finally
            {
                await budget.CancelAsync().ConfigureAwait(false);
                body.Writer.TryComplete(new OperationCanceledException("WebSocket peer delivery ended."));
            }
        }
        return Results.Empty;

        async Task ReadBodyAsync()
        {
            try
            {
                while (true)
                {
                    var frame = await WebSocketPeerFrames.ReceiveAsync(socket, correlation, budget.Token).ConfigureAwait(false);
                    if (frame.Type == WebSocketMessageType.SyncRequestEnd)
                    {
                        body.Writer.TryComplete();
                        return;
                    }
                    if (frame.Type != WebSocketMessageType.SyncRequestChunk)
                        throw new InvalidDataException("Unexpected WebSocket peer request frame.");
                    await body.Writer.WriteAsync(frame.Payload, budget.Token).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                body.Writer.TryComplete(exception);
                if (!completed) await budget.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }
}
