using System.Text.Json;
using System.Threading.Channels;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
// AppJsonContext est dans namespace SlimFaas - accessible via using implicite (même assembly)

namespace SlimFaas.WebSocket;

/// <summary>
/// Service qui envoie une requête asynchrone à un client WebSocket et attend son callback.
/// Utilisé par <see cref="SlimFaas.Workers.SlimQueuesWorker"/> à la place d'un appel HTTP classique.
/// </summary>
public interface IWebSocketSendClient
{
    /// <summary>
    /// Envoie la requête au client WebSocket le moins chargé pour la fonction donnée.
    /// Retourne le code HTTP simulé (venant du callback du client).
    /// </summary>
    Task<int> SendAsync(
        string functionName,
        CustomRequest customRequest,
        string elementId,
        bool isLastTry,
        int tryNumber,
        CancellationToken ct = default);

    /// <summary>
    /// Transmet un évènement publish/subscribe à tous les clients WebSocket abonnés.
    /// </summary>
    Task PublishEventAsync(
        string functionName,
        CustomRequest customRequest,
        string eventName,
        CancellationToken ct = default);

    /// <summary>
    /// Envoie une requête HTTP synchrone vers un client WebSocket en mode streaming binaire.
    /// Le body de la requête est streamé depuis <paramref name="requestBodyStream"/>.
    /// Retourne le status code, les headers et un stream pour le body de la réponse.
    /// </summary>
    Task<(int StatusCode, Dictionary<string, string[]> Headers, ChannelReader<byte[]> BodyChunks, Func<Task> WaitForEnd)>
        SendSyncRequestStreamAsync(
            string functionName,
            string method,
            string path,
            string query,
            Dictionary<string, string[]> headers,
            Stream? requestBodyStream,
            CancellationToken ct = default);
}

public class WebSocketSendClient : IWebSocketSendClient
{
    private readonly WebSocketConnectionRegistry _registry;
    private readonly ILogger<WebSocketSendClient> _logger;
    private readonly NetworkActivityTracker _activityTracker;

    public WebSocketSendClient(
        WebSocketConnectionRegistry registry,
        ILogger<WebSocketSendClient> logger,
        NetworkActivityTracker activityTracker)
    {
        _registry = registry;
        _logger = logger;
        _activityTracker = activityTracker;
    }

    public async Task<int> SendAsync(
        string functionName,
        CustomRequest customRequest,
        string elementId,
        bool isLastTry,
        int tryNumber,
        CancellationToken ct = default)
    {
        int maxPerPod = _registry.GetConfiguration(functionName)?.NumberParallelRequestPerPod ?? int.MaxValue;
        var connection = _registry.SelectNextRoundRobin(functionName, maxPerPod);
        if (connection == null)
        {
            _logger.LogWarning("No WebSocket client available for function {FunctionName}", functionName);
            return 503;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.PendingCallbacks[elementId] = tcs;

        try
        {
            var payload = new AsyncRequestPayload
            {
                ElementId = elementId,
                Method = customRequest.Method,
                Path = customRequest.Path,
                Query = customRequest.Query,
                Headers = customRequest.Headers.ToDictionary(h => h.Key, h => h.Values.Select(v => v ?? "").ToArray()),
                Body = customRequest.Body != null ? Convert.ToBase64String(customRequest.Body) : null,
                IsLastTry = isLastTry,
                TryNumber = tryNumber,
            };

            var envelope = new WebSocketEnvelope
            {
                Type = WebSocketMessageType.AsyncRequest,
                CorrelationId = elementId,
                Payload = JsonSerializer.SerializeToElement(payload, AppJsonContext.Default.AsyncRequestPayload),
            };

            await connection.SendAsync(envelope, ct);
            _logger.LogDebug("AsyncRequest sent via WebSocket to {FunctionName}/{ConnectionId} elementId={ElementId}", functionName, connection.ConnectionId, elementId);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(300));
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("WebSocket async request timed out for {FunctionName}/{ElementId}", functionName, elementId);
            return 504;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending WebSocket async request to {FunctionName}/{ElementId}", functionName, elementId);
            return 500;
        }
        finally
        {
            connection.PendingCallbacks.TryRemove(elementId, out _);
        }
    }

    public async Task PublishEventAsync(
        string functionName,
        CustomRequest customRequest,
        string eventName,
        CancellationToken ct = default)
    {
        var connections = _registry.GetConnections(functionName);
        if (connections.Count == 0) return;

        var payload = new PublishEventPayload
        {
            EventName = eventName,
            Method = customRequest.Method,
            Path = customRequest.Path,
            Query = customRequest.Query,
            Headers = customRequest.Headers.ToDictionary(h => h.Key, h => h.Values.Select(v => v ?? "").ToArray()),
            Body = customRequest.Body != null ? Convert.ToBase64String(customRequest.Body) : null,
        };

        var envelope = new WebSocketEnvelope
        {
            Type = WebSocketMessageType.PublishEvent,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Payload = JsonSerializer.SerializeToElement(payload, AppJsonContext.Default.PublishEventPayload),
        };

        await Task.WhenAll(connections.Select(c => SafeSendPublishEventAsync(functionName, c, envelope, ct)));
    }

    public async Task<(int StatusCode, Dictionary<string, string[]> Headers, ChannelReader<byte[]> BodyChunks, Func<Task> WaitForEnd)>
        SendSyncRequestStreamAsync(
            string functionName,
            string method,
            string path,
            string query,
            Dictionary<string, string[]> headers,
            Stream? requestBodyStream,
            CancellationToken ct = default)
    {
        int maxPerPod = _registry.GetConfiguration(functionName)?.NumberParallelRequestPerPod ?? int.MaxValue;
        var connection = _registry.SelectNextRoundRobin(functionName, maxPerPod);
        if (connection == null)
        {
            _logger.LogWarning("No WebSocket client available for sync stream to {FunctionName}", functionName);
            throw new InvalidOperationException($"No WebSocket client available for function '{functionName}'");
        }

        var correlationId = Guid.NewGuid().ToString("D"); // 36 chars format for binary frames

        var pendingStream = new PendingSyncStream();
        connection.PendingSyncStreams[correlationId] = pendingStream;

        try
        {
            // 1. Envoie SyncRequestStart avec method + path + query + headers comme payload JSON
            var startPayload = new SyncRequestStartPayload
            {
                Method = method,
                Path = path,
                Query = query,
                Headers = headers,
            };
            var startJson = JsonSerializer.SerializeToUtf8Bytes(startPayload, AppJsonContext.Default.SyncRequestStartPayload);
            var startFrame = BinaryFrame.Encode(WebSocketMessageType.SyncRequestStart, correlationId, startJson);
            await connection.SendBinaryAsync(startFrame, ct);

            // 2. Stream le body de la requête en chunks
            if (requestBodyStream != null)
            {
                var buffer = new byte[32 * 1024]; // 32 KB chunks
                int bytesRead;
                while ((bytesRead = await requestBodyStream.ReadAsync(buffer, ct)) > 0)
                {
                    var chunkFrame = BinaryFrame.Encode(
                        WebSocketMessageType.SyncRequestChunk,
                        correlationId,
                        buffer.AsSpan(0, bytesRead));
                    await connection.SendBinaryAsync(chunkFrame, ct);
                }
            }

            // 3. Envoie SyncRequestEnd
            var endFrame = BinaryFrame.Encode(WebSocketMessageType.SyncRequestEnd, correlationId, BinaryFrame.FlagEndOfStream);
            await connection.SendBinaryAsync(endFrame, ct);

            _logger.LogDebug(
                "SyncRequest stream sent to {FunctionName}/{ConnectionId} correlationId={CorrelationId}",
                functionName, connection.ConnectionId, correlationId);

            // 4. Attend SyncResponseStart du client (avec timeout)
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, pendingStream.Cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(300));

            var responseStart = await pendingStream.ResponseStartTcs.Task.WaitAsync(cts.Token);

            return (
                responseStart.StatusCode,
                responseStart.Headers,
                pendingStream.ResponseChunks.Reader,
                async () =>
                {
                    try { await pendingStream.ResponseEndTcs.Task.WaitAsync(cts.Token); }
                    finally
                    {
                        connection.PendingSyncStreams.TryRemove(correlationId, out _);
                        cts.Dispose();
                    }
                }
            );
        }
        catch (Exception)
        {
            connection.PendingSyncStreams.TryRemove(correlationId, out _);

            // Envoie SyncCancel au client pour libérer ses ressources
            try
            {
                var cancelFrame = BinaryFrame.Encode(WebSocketMessageType.SyncCancel, correlationId);
                await connection.SendBinaryAsync(cancelFrame, ct);
            }
            catch { /* ignore */ }

            throw;
        }
    }

    private async Task SafeSendPublishEventAsync(
        string functionName,
        WebSocketClientConnection connection,
        WebSocketEnvelope envelope,
        CancellationToken ct)
    {
        try
        {
            _activityTracker.Record(NetworkActivityTracker.EventTypes.EventPublish, NetworkActivityTracker.Actors.SlimFaas, functionName, targetPod: connection.ConnectionId);
            await connection.SendAsync(envelope, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to send WebSocket message to {ConnectionId}", connection.ConnectionId); }
    }
}
