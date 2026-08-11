using System.Collections.Concurrent;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.WebSocket;

internal sealed record TrackedWebSocketRequest(
    long Generation,
    string FunctionName,
    string Id,
    CancellationTokenSource Cancellation);

internal readonly record struct CompletedWebSocketRequest(
    TrackedWebSocketRequest Request,
    int StatusCode,
    Exception? Error);

/// <summary>
/// Dispatches durable async queue items to registered WebSocket functions.
/// Queue commits and request completions wake the worker; the configured polling delay is only a fallback.
/// </summary>
public class WebSocketQueuesWorker(
    ISlimFaasQueue slimFaasQueue,
    WebSocketConnectionRegistry registry,
    IWebSocketSendClient webSocketSendClient,
    IWebSocketFunctionRepository functionRepository,
    HistoryHttpMemoryService historyHttpService,
    ILogger<WebSocketQueuesWorker> logger,
    IMasterService masterService,
    IOptions<WorkersOptions> workersOptions,
    SlimDataQueueSignal? queueSignal = null)
    : BackgroundService
{
    private readonly int _maximumIdleWaitMilliseconds = workersOptions.Value.QueuesDelayMilliseconds;
    private readonly SlimDataQueueSignal _queueSignal = queueSignal ?? new SlimDataQueueSignal();
    private readonly long _initialSignalVersion = queueSignal?.Version ?? 0;
    private readonly ConcurrentQueue<CompletedWebSocketRequest> _completedRequests = new();
    private long _trackingGeneration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var processingRequests = new Dictionary<string, Dictionary<string, TrackedWebSocketRequest>>(StringComparer.Ordinal);
        var lastCallCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        // Capture at construction time so a durable mutation committed after DI has built the
        // worker, but just before ExecuteAsync is scheduled, cannot be missed.
        long observedSignalVersion = _initialSignalVersion;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    long previousVersion = observedSignalVersion;
                    observedSignalVersion = await _queueSignal.WaitForChangeAsync(
                        observedSignalVersion,
                        TimeSpan.FromMilliseconds(_maximumIdleWaitMilliseconds),
                        stoppingToken).ConfigureAwait(false);
                    AsyncQueueTelemetry.RecordWake(
                        "websocket",
                        observedSignalVersion == previousVersion
                            ? "fallback"
                            : _completedRequests.IsEmpty ? "mutation" : "completion");
                    using IDisposable cycle = AsyncQueueTelemetry.MeasureCycle("websocket");
                    if (!masterService.IsMaster)
                    {
                        ClearLocalTracking(processingRequests);
                        continue;
                    }

                    await DrainCompletedRequestsAsync(processingRequests).ConfigureAwait(false);
                    foreach (DeploymentInformation deployment in functionRepository.GetVirtualDeployments())
                    {
                        await ProcessQueueAsync(
                            deployment,
                            processingRequests,
                            lastCallCounters,
                            stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Error in WebSocketQueuesWorker");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            ClearLocalTracking(processingRequests);
        }
    }

    private async Task ProcessQueueAsync(
        DeploymentInformation deployment,
        Dictionary<string, Dictionary<string, TrackedWebSocketRequest>> processingRequests,
        Dictionary<string, int> lastCallCounters,
        CancellationToken stoppingToken)
    {
        string functionName = deployment.Deployment;
        Dictionary<string, TrackedWebSocketRequest> active = GetOrAdd(processingRequests, functionName);
        lastCallCounters.TryAdd(functionName, 0);
        int connectionCount = registry.GetConnections(functionName).Count;
        if (connectionCount == 0)
            return;

        QueueDispatchState queueState;
        using (AsyncQueueTelemetry.MeasureSnapshot("websocket"))
        {
            queueState = await slimFaasQueue
                .GetDispatchStateAsync(functionName)
                .ConfigureAwait(false);
        }

        int maximumParallelism = Math.Min(
            deployment.NumberParallelRequest,
            connectionCount * deployment.NumberParallelRequestPerPod);
        int availableSlots = Math.Max(0, maximumParallelism - active.Count);
        UpdateLastCallIfWorkRemains(
            deployment.Replicas,
            lastCallCounters,
            functionName,
            active.Count,
            queueState.TotalPending);
        if (deployment.Replicas == 0 || queueState.Available <= 0 || availableSlots <= 0)
            return;

        IList<QueueData>? items;
        using (AsyncQueueTelemetry.MeasureDequeue("websocket"))
        {
            items = await slimFaasQueue.DequeueAsync(
                functionName,
                Math.Min(availableSlots, queueState.Available)).ConfigureAwait(false);
        }
        if (items is null)
            return;
        foreach (QueueData item in items)
        {
            CustomRequest customRequest;
            try
            {
                customRequest = MemoryPackSerializer.Deserialize<CustomRequest>(item.Data);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to deserialize CustomRequest for WebSocket function {FunctionName}",
                    functionName);
                await slimFaasQueue.ListCallbackAsync(
                    functionName,
                    new ListQueueItemStatus
                    {
                        Items = [new QueueItemStatus(item.Id, StatusCodes.Status500InternalServerError)]
                    }).ConfigureAwait(false);
                continue;
            }

            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
            var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var tracked = new TrackedWebSocketRequest(
                Volatile.Read(ref _trackingGeneration),
                functionName,
                item.Id,
                requestCancellation);
            active[item.Id] = tracked;
            Task<int> responseTask = webSocketSendClient.SendAsync(
                functionName,
                customRequest,
                item.Id,
                item.IsLastTry,
                item.TryNumber,
                requestCancellation.Token);
            _ = ObserveCompletionAsync(responseTask, tracked);
        }
    }

    private async Task ObserveCompletionAsync(
        Task<int> responseTask,
        TrackedWebSocketRequest request)
    {
        var statusCode = StatusCodes.Status500InternalServerError;
        Exception? error = null;
        try
        {
            statusCode = await responseTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            request.Cancellation.Dispose();
        }

        _completedRequests.Enqueue(new CompletedWebSocketRequest(request, statusCode, error));
        _queueSignal.Pulse();
    }

    private async Task DrainCompletedRequestsAsync(
        Dictionary<string, Dictionary<string, TrackedWebSocketRequest>> processingRequests)
    {
        long generation = Volatile.Read(ref _trackingGeneration);
        var callbacks = new Dictionary<string, List<QueueItemStatus>>(StringComparer.Ordinal);
        while (_completedRequests.TryDequeue(out CompletedWebSocketRequest completed))
        {
            TrackedWebSocketRequest request = completed.Request;
            if (processingRequests.TryGetValue(request.FunctionName, out var active))
                active.Remove(request.Id);
            if (request.Generation != generation)
                continue;
            historyHttpService.SetTickLastCall(request.FunctionName, DateTime.UtcNow.Ticks);
            if (completed.Error is not null)
            {
                logger.LogWarning(
                    completed.Error,
                    "WebSocket async request failed for {FunctionName}/{ElementId}",
                    request.FunctionName,
                    request.Id);
            }
            else
            {
                logger.LogDebug(
                    "WebSocket async completed for {FunctionName} elementId={ElementId} statusCode={StatusCode}",
                    request.FunctionName,
                    request.Id,
                    completed.StatusCode);
            }
            if (completed.StatusCode == StatusCodes.Status202Accepted && completed.Error is null)
                continue;
            if (!callbacks.TryGetValue(request.FunctionName, out List<QueueItemStatus>? statuses))
            {
                statuses = [];
                callbacks.Add(request.FunctionName, statuses);
            }
            statuses.Add(new QueueItemStatus(request.Id, completed.StatusCode));
        }

        foreach ((string functionName, List<QueueItemStatus> statuses) in callbacks)
        {
            await slimFaasQueue.ListCallbackAsync(
                functionName,
                new ListQueueItemStatus { Items = statuses }).ConfigureAwait(false);
        }
    }

    private void UpdateLastCallIfWorkRemains(
        int replicas,
        Dictionary<string, int> counters,
        string functionName,
        int activeRequests,
        int queueLength)
    {
        int counterLimit = replicas == 0 ? 10 : 40;
        counters[functionName]++;
        if (counters[functionName] <= counterLimit)
            return;
        counters[functionName] = 0;
        if (queueLength > 0 || activeRequests > 0)
            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
    }

    private void ClearLocalTracking(
        Dictionary<string, Dictionary<string, TrackedWebSocketRequest>> processingRequests)
    {
        if (processingRequests.Values.All(active => active.Count == 0))
            return;
        Interlocked.Increment(ref _trackingGeneration);
        foreach (Dictionary<string, TrackedWebSocketRequest> active in processingRequests.Values)
        {
            foreach (TrackedWebSocketRequest request in active.Values)
            {
                try
                {
                    request.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            active.Clear();
        }
    }

    private static Dictionary<string, TrackedWebSocketRequest> GetOrAdd(
        IDictionary<string, Dictionary<string, TrackedWebSocketRequest>> dictionary,
        string key)
    {
        if (!dictionary.TryGetValue(key, out Dictionary<string, TrackedWebSocketRequest>? value))
        {
            value = new Dictionary<string, TrackedWebSocketRequest>(StringComparer.Ordinal);
            dictionary.Add(key, value);
        }
        return value;
    }
}
