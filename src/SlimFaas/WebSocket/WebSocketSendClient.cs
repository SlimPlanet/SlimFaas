using System.Text.Json;
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
}

public class WebSocketSendClient : IWebSocketSendClient
{
    private readonly WebSocketConnectionRegistry _registry;
    private readonly ILogger<WebSocketSendClient> _logger;

    public WebSocketSendClient(
        WebSocketConnectionRegistry registry,
        ILogger<WebSocketSendClient> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public async Task<int> SendAsync(
        string functionName,
        CustomRequest customRequest,
        string elementId,
        bool isLastTry,
        int tryNumber,
        CancellationToken ct = default)
    {
        var connection = _registry.SelectLeastBusy(functionName);
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

        await Task.WhenAll(connections.Select(c => SafeSendAsync(c, envelope, ct)));
    }

    private async Task SafeSendAsync(
        WebSocketClientConnection connection,
        WebSocketEnvelope envelope,
        CancellationToken ct)
    {
        try { await connection.SendAsync(envelope, ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to send WebSocket message to {ConnectionId}", connection.ConnectionId); }
    }
}
