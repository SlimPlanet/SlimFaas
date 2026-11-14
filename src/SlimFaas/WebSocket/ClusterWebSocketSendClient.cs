using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.WebSocket;

public sealed class ClusterWebSocketSendClient : IWebSocketSendClient, IDisposable
{
    public const string HttpClientName = "WebSocketPeer";
    private readonly WebSocketSendClient _local;
    private readonly IWebSocketClusterDirectory _directory;
    private readonly IWebSocketFunctionRepository _functions;
    private readonly IReplicasService _replicas;
    private readonly SlimFaasOptions _options;
    private readonly INamespaceProvider _namespace;
    private readonly ILogger<ClusterWebSocketSendClient> _logger;
    private readonly HttpClient _http;
    private readonly HttpMessageInvoker _sync;
    private readonly ConcurrentDictionary<string, int> _roundRobin = new(StringComparer.OrdinalIgnoreCase);

    public ClusterWebSocketSendClient(WebSocketSendClient local, IWebSocketClusterDirectory directory,
        IWebSocketFunctionRepository functions, IReplicasService replicas, IOptions<SlimFaasOptions> options,
        INamespaceProvider namespaceProvider, IHttpClientFactory clients, ILogger<ClusterWebSocketSendClient> logger)
    {
        _local = local;
        _directory = directory;
        _functions = functions;
        _replicas = replicas;
        _options = options.Value;
        _namespace = namespaceProvider;
        _logger = logger;
        _http = clients.CreateClient(HttpClientName);
        // HTTP response-duration metrics wrap upgraded streams as read-only.
        // A WebSocket handshake needs an unwrapped duplex transport.
#pragma warning disable CA2000 // _sync owns and disposes its handler.
        var handler = InternalHttpClientHandler.Create(_options.AllowUnsecureSsl);
#pragma warning restore CA2000
        handler.AllowAutoRedirect = false;
        _sync = new HttpMessageInvoker(handler);
    }

    private async Task<WebSocketDirectoryConnection> SelectAsync(string function, CancellationToken cancellationToken)
    {
        var connections = _directory.Connections.Where(connection =>
            connection.Function.Equals(function, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (connections.Length == 0)
        {
            await _directory.RefreshAsync(cancellationToken).ConfigureAwait(false);
            connections = _directory.Connections.Where(connection =>
                connection.Function.Equals(function, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (connections.Length == 0)
            throw new InvalidOperationException($"No connected WebSocket owner for '{function}'.");
        int next = _roundRobin.AddOrUpdate(function, 0, (_, previous) => unchecked(previous + 1));
        return connections[(int)((uint)next % (uint)connections.Length)];
    }

    private Uri Endpoint(WebSocketDirectoryConnection connection, string operation, bool webSocket = false)
    {
        var pod = _replicas.Deployments.SlimFaas.Pods.FirstOrDefault(candidate => candidate.Name == connection.Node);
        var endpoint = pod is null ? null : SlimFaasPeerEndpoint.Resolve(pod, _options, _namespace.CurrentNamespace);
        if (endpoint is null)
            throw new InvalidOperationException("WebSocket owner is absent from cluster membership.");
        return new UriBuilder(endpoint)
        {
            Path = "/internal/websocket/" + operation, Query = "",
            Scheme = webSocket ? endpoint.Scheme == "https" ? "wss" : "ws" : endpoint.Scheme
        }.Uri;
    }

    private WebSocketPeerContext Proof(WebSocketDirectoryConnection connection, string operation,
        ReadOnlySpan<byte> payload, int timeoutSeconds, string? sourcePod = null, string? correlationId = null) =>
        new(_directory.Node, connection.Node, connection.Incarnation, operation, connection.Function,
            connection.Fingerprint, Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.AddSeconds(timeoutSeconds).Ticks, Convert.ToHexString(SHA256.HashData(payload)),
            sourcePod, correlationId);

    private (string Context, string Signature) Sign(WebSocketPeerContext proof)
    {
        var owner = _directory.Owner(proof.Owner);
        if (owner is null || owner.Incarnation != proof.Incarnation || owner.ExpiresAtTicks <= DateTime.UtcNow.Ticks)
            throw new InvalidOperationException("WebSocket owner lease has expired.");
        return WebSocketPeerAuthorization.Sign(proof, owner);
    }

    public async Task<int> SendAsync(string functionName, CustomRequest customRequest, string elementId,
        bool isLastTry, int tryNumber, CancellationToken ct = default)
    {
        try
        {
            var connection = await SelectAsync(functionName, ct).ConfigureAwait(false);
            if (connection.Node == _directory.Node)
                return await _local.SendAsync(connection.Function, customRequest, elementId, isLastTry, tryNumber, ct)
                    .ConfigureAwait(false);
            int timeout = _functions.GetVirtualDeployments().First(function => function.Deployment == connection.Function)
                .Configuration.DefaultAsync.HttpTimeout;
            var payload = new WebSocketPeerRequest(customRequest, elementId, tryNumber, isLastTry, null, []);
            return await PostAsync(connection, "async", payload, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            _logger.LogPeerDeliveryFailed(exception, "async", functionName);
            return 504;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or JsonException)
        {
            _logger.LogPeerDeliveryFailed(exception, "async", functionName);
            return 503;
        }
    }

    private async Task<int> PostAsync(WebSocketDirectoryConnection connection, string operation,
        WebSocketPeerRequest request, int timeoutSeconds, CancellationToken cancellationToken,
        string? sourcePod = null, string? correlationId = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, WebSocketPeerJsonContext.Default.WebSocketPeerRequest);
        var proof = Proof(connection, operation, bytes, timeoutSeconds, sourcePod, correlationId);
        var signed = Sign(proof);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint(connection, operation));
        message.Headers.Add(WebSocketPeerAuthorization.ContextHeader, signed.Context);
        message.Headers.Add(WebSocketPeerAuthorization.SignatureHeader, signed.Signature);
        message.Content = new ByteArrayContent(bytes);
        message.Content.Headers.ContentType = new("application/json");
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, budget.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(WebSocketPeerJsonContext.Default.WebSocketPeerResult, budget.Token)
            .ConfigureAwait(false) ?? throw new IOException("Missing WebSocket peer delivery result.");
        return result.StatusCode;
    }

    public async Task PublishEventAsync(string functionName, CustomRequest customRequest, string eventName,
        string? activitySourcePod = null, string? activityCorrelationId = null, CancellationToken ct = default)
    {
        var connections = _directory.Connections.Where(connection =>
            connection.Function.Equals(functionName, StringComparison.OrdinalIgnoreCase)).ToArray();
        await Parallel.ForEachAsync(connections.GroupBy(connection => connection.Node, StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (group, token) =>
        {
            try
            {
                var ids = group.Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
                if (group.Key == _directory.Node)
                    await _local.PublishEventToConnectionsAsync(group.First().Function, customRequest, eventName, ids,
                        activitySourcePod, activityCorrelationId, token).ConfigureAwait(false);
                else
                {
                    var result = await PostAsync(group.First(), "event",
                        new(customRequest, null, 0, false, eventName, ids.ToArray()), 300, token,
                        activitySourcePod, activityCorrelationId).ConfigureAwait(false);
                    if (result >= 400)
                        _logger.LogPeerEventRejected(functionName, group.Key, result);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or
                JsonException or OperationCanceledException)
            {
                _logger.LogPeerDeliveryFailed(exception, "event", functionName);
            }
        }).ConfigureAwait(false);
    }

    public async Task<(int StatusCode, Dictionary<string, string[]> Headers, ChannelReader<byte[]> BodyChunks, Func<Task> WaitForEnd)>
        SendSyncRequestStreamAsync(string functionName, string method, string path, string query,
            Dictionary<string, string[]> headers, Stream? requestBodyStream,
            string? activitySourcePod = null, string? activityCorrelationId = null, CancellationToken ct = default)
    {
        var connection = await SelectAsync(functionName, ct).ConfigureAwait(false);
        if (connection.Node == _directory.Node)
            return await _local.SendSyncRequestStreamAsync(connection.Function, method, path, query, headers,
                requestBodyStream, activitySourcePod, activityCorrelationId, ct).ConfigureAwait(false);

        var start = JsonSerializer.SerializeToUtf8Bytes(new SyncRequestStartPayload
        {
            Method = method, Path = path, Query = query, Headers = WebSocketPeerHeaders.Filter(headers)
        }, AppJsonContext.Default.SyncRequestStartPayload);
        if (start.Length + BinaryFrame.HeaderSize > WebSocketPeerFrames.MaximumFrameBytes)
            throw new InvalidDataException("Synchronous relay request headers exceed the size limit.");
        var proof = Proof(connection, "sync", start, 300, activitySourcePod, activityCorrelationId);
        var signed = Sign(proof);
        string correlation = new Guid(proof.Nonce).ToString("D");
#pragma warning disable CA2000 // The returned completion task owns the socket and cancellation sources.
        var socket = new ClientWebSocket();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var uploadCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
#pragma warning restore CA2000
        budget.CancelAfter(TimeSpan.FromSeconds(300));
        var relayToken = budget.Token;
        socket.Options.SetRequestHeader(WebSocketPeerAuthorization.ContextHeader, signed.Context);
        socket.Options.SetRequestHeader(WebSocketPeerAuthorization.SignatureHeader, signed.Signature);
        var responseStart = new TaskCompletionSource<SyncResponseStartPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        var chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });
        Task upload = Task.CompletedTask;
        Task completion = Task.CompletedTask;
        int cleaned = 0;
        bool completed = false;
        try
        {
            await socket.ConnectAsync(Endpoint(connection, "sync", webSocket: true), _sync, relayToken).ConfigureAwait(false);
            await WebSocketPeerFrames.SendAsync(socket, WebSocketMessageType.SyncRequestStart, correlation, start, relayToken)
                .ConfigureAwait(false);
            upload = UploadAsync();
            completion = ReadResponseAsync();
            _ = completion.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var response = await responseStart.Task.WaitAsync(relayToken).ConfigureAwait(false);
            return (response.StatusCode, WebSocketPeerHeaders.Filter(response.Headers), chunks.Reader, () => completion);
        }
        catch
        {
            await CleanupAsync().ConfigureAwait(false);
            try { await completion.ConfigureAwait(false); }
            catch (Exception exception)
            { _logger.LogPeerDeliveryFailed(exception, "sync completion", functionName); }
            throw;
        }

        async Task UploadAsync()
        {
            try
            {
                await WebSocketPeerFrames.SendBodyAsync(socket, correlation, requestBodyStream, uploadCancellation.Token, relayToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (uploadCancellation.IsCancellationRequested && !budget.IsCancellationRequested)
            {
                // A completed response stops only further upload reads. It must
                // not cancel the successful response's still-pending headers.
            }
            catch (Exception exception)
            {
                responseStart.TrySetException(exception);
                await budget.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }

        async Task ReadResponseAsync()
        {
            try
            {
                bool started = false;
                while (true)
                {
                    var frame = await WebSocketPeerFrames.ReceiveAsync(socket, correlation, relayToken).ConfigureAwait(false);
                    switch (frame.Type)
                    {
                        case WebSocketMessageType.SyncResponseStart when !started:
                            var response = JsonSerializer.Deserialize(frame.Payload, AppJsonContext.Default.SyncResponseStartPayload)
                                ?? throw new InvalidDataException("Missing WebSocket peer response headers.");
                            responseStart.TrySetResult(response);
                            started = true;
                            break;
                        case WebSocketMessageType.SyncResponseChunk when started:
                            await chunks.Writer.WriteAsync(frame.Payload, relayToken).ConfigureAwait(false);
                            break;
                        case WebSocketMessageType.SyncResponseEnd when started:
                            await uploadCancellation.CancelAsync().ConfigureAwait(false);
                            try { await upload.ConfigureAwait(false); }
                            catch (OperationCanceledException) when (uploadCancellation.IsCancellationRequested) { }
                            chunks.Writer.TryComplete();
                            completed = true;
                            return;
                        default:
                            throw new InvalidDataException("Unexpected WebSocket peer response frame.");
                    }
                }
            }
            catch (Exception exception)
            {
                responseStart.TrySetException(exception);
                chunks.Writer.TryComplete(exception);
                _logger.LogPeerDeliveryFailed(exception, "sync", functionName);
                throw;
            }
            finally
            {
                await CleanupAsync().ConfigureAwait(false);
            }
        }

        async Task CleanupAsync()
        {
            if (Interlocked.Exchange(ref cleaned, 1) != 0) return;
            try
            {
                await uploadCancellation.CancelAsync().ConfigureAwait(false);
                if (!completed) socket.Abort();
                try { await upload.ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
                { _logger.LogPeerDeliveryFailed(exception, "sync upload", functionName); }
                if (completed)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Delivery complete", closing.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
                    {
                        _logger.LogPeerDeliveryFailed(exception, "sync close", functionName);
                        socket.Abort();
                    }
                }
            }
            finally
            {
                if (!completed) await budget.CancelAsync().ConfigureAwait(false);
                socket.Dispose();
                uploadCancellation.Dispose();
                budget.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _sync.Dispose();
        _http.Dispose();
    }
}
