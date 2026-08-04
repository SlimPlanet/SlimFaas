using System.Collections.Concurrent;
using System.Threading.Channels;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.ClusterFiles;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas;

internal sealed record TrackedHttpRequest(
    long Generation,
    string FunctionName,
    CustomRequest CustomRequest,
    string Id,
    string TargetIp,
    Stream? OffloadedStream,
    CancellationTokenSource Cancellation,
    IReadOnlySet<int> HttpStatusRetries);

internal readonly record struct CompletedHttpRequest(
    TrackedHttpRequest Request,
    int StatusCode,
    Exception? Error);

internal readonly record struct Awaiting202Request(string Id, string TargetIp);

internal sealed record PreparedHttpMessage(
    QueueData Message,
    CustomRequest Request,
    Stream? OffloadedStream);

public class SlimQueuesWorker(
    ISlimFaasQueue slimFaasQueue,
    IReplicasService replicasService,
    HistoryHttpMemoryService historyHttpService,
    ILogger<SlimQueuesWorker> logger,
    ISendClient sendClient,
    ISlimDataStatus slimDataStatus,
    IMasterService masterService,
    IClusterFileSync fileSync,
    IDatabaseService db,
    IOptions<WorkersOptions> workersOptions,
    NetworkActivityTracker activityTracker,
    SlimDataQueueSignal? queueSignal = null)
    : BackgroundService
{
    public const string SlimfaasElementId = "SlimFaas-Element-Id";
    public const string SlimfaasTryNumber = "SlimFaas-Try-Number";
    public const string SlimfaasLastTry = "Slimfaas-Last-Try";
    private const int OffloadedBodyUnavailableStatusCode = 500;
    private const int MaximumConcurrentOffloadPreparations = 32;
    private const int MaximumOffloadCleanupBatchSize = 32;

    private readonly int _maximumIdleWaitMilliseconds = workersOptions.Value.QueuesDelayMilliseconds;
    private readonly SlimDataQueueSignal _queueSignal = queueSignal ?? new SlimDataQueueSignal();
    private readonly long _initialSignalVersion = queueSignal?.Version ?? 0;
    private readonly ConcurrentQueue<CompletedHttpRequest> _completedRequests = new();
    private readonly Channel<string> _terminalOffloadCleanup = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private long _trackingGeneration;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long observedSignalVersion = _initialSignalVersion;
        await slimDataStatus.WaitForReadyAsync().ConfigureAwait(false);
        var processingRequests = new Dictionary<string, Dictionary<string, TrackedHttpRequest>>(StringComparer.Ordinal);
        var awaiting202Requests = new Dictionary<string, List<Awaiting202Request>>(StringComparer.Ordinal);
        var lastCallCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        Task cleanupTask = RunTerminalOffloadCleanupAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                long previousVersion = observedSignalVersion;
                observedSignalVersion = await _queueSignal.WaitForChangeAsync(
                    observedSignalVersion,
                    TimeSpan.FromMilliseconds(_maximumIdleWaitMilliseconds),
                    stoppingToken).ConfigureAwait(false);
                AsyncQueueTelemetry.RecordWake(
                    "http",
                    observedSignalVersion == previousVersion
                        ? "fallback"
                        : _completedRequests.IsEmpty ? "mutation" : "completion");
                using IDisposable cycle = AsyncQueueTelemetry.MeasureCycle("http");
                await DoOneCycleAsync(
                    lastCallCounters,
                    processingRequests,
                    awaiting202Requests,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ClearLocalTracking(processingRequests, awaiting202Requests);
            _terminalOffloadCleanup.Writer.TryComplete();
            await cleanupTask.ConfigureAwait(false);
        }
    }

    private async Task DoOneCycleAsync(
        Dictionary<string, int> lastCallCounters,
        Dictionary<string, Dictionary<string, TrackedHttpRequest>> processingRequests,
        Dictionary<string, List<Awaiting202Request>> awaiting202Requests,
        CancellationToken stoppingToken)
    {
        try
        {
            if (!masterService.IsMaster)
            {
                ClearLocalTracking(processingRequests, awaiting202Requests);
                return;
            }

            await DrainCompletedRequestsAsync(processingRequests, awaiting202Requests).ConfigureAwait(false);
            DeploymentsInformations deployments = replicasService.Deployments;
            foreach (DeploymentInformation function in deployments.Functions)
            {
                string functionName = function.Deployment;
                Dictionary<string, TrackedHttpRequest> active = GetOrAdd(processingRequests, functionName);
                List<Awaiting202Request> awaiting202 = GetOrAdd(awaiting202Requests, functionName);
                lastCallCounters.TryAdd(functionName, 0);

                QueueDispatchState queueState;
                using (AsyncQueueTelemetry.MeasureSnapshot("http"))
                {
                    queueState = await slimFaasQueue
                        .GetDispatchStateAsync(functionName)
                        .ConfigureAwait(false);
                }
                CompleteAwaiting202(functionName, awaiting202, queueState.RunningReservations);

                int readyPods = function.Pods?.Count(pod =>
                    pod.Ready == true && !string.IsNullOrEmpty(pod.Ip)) ?? 1;
                int maximumParallelism = Math.Min(
                    function.NumberParallelRequest,
                    readyPods * function.NumberParallelRequestPerPod);
                int availableSlots = Math.Max(0, maximumParallelism - active.Count);
                UpdateLastCallIfWorkRemains(
                    function.Replicas,
                    lastCallCounters,
                    functionName,
                    active.Count,
                    queueState.TotalPending);

                if (function.Replicas == 0 || queueState.Available <= 0 || availableSlots <= 0)
                    continue;
                bool hasReadyContainer = function.Pods?.Any(pod => pod.Ready == true) == true;
                if (!hasReadyContainer || !function.EndpointReady)
                    continue;

                await DispatchRequestsAsync(
                    active,
                    Math.Min(availableSlots, queueState.Available),
                    queueState.RunningReservations,
                    function,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Global Error in SlimFaas Worker");
        }
    }

    private async Task DispatchRequestsAsync(
        Dictionary<string, TrackedHttpRequest> active,
        int maximumMessages,
        IReadOnlyList<QueueRunningReservation> runningReservations,
        DeploymentInformation function,
        CancellationToken stoppingToken)
    {
        string functionName = function.Deployment;
        var proxy = new Proxy(replicasService, functionName);
        string[] alreadyUsedIps = runningReservations
            .Select(reservation => reservation.ReservedIp)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToArray();
        IList<string> reservedIps = proxy.ReserveNextIPs(
            function.NumberParallelRequestPerPod,
            maximumMessages,
            alreadyUsedIps);
        if (reservedIps.Count == 0)
        {
            logger.LogDebug("All pods saturated for {FunctionDeployment}, skipping dequeue", functionName);
            return;
        }

        IList<QueueData>? messages;
        using (AsyncQueueTelemetry.MeasureDequeue("http"))
        {
            messages = await slimFaasQueue
                .DequeueAsync(functionName, reservedIps.Count, reservedIps)
                .ConfigureAwait(false);
        }
        if (messages is null || messages.Count == 0)
            return;

        PreparedHttpMessage?[] preparedMessages = await PrepareMessagesAsync(
            functionName,
            messages,
            stoppingToken).ConfigureAwait(false);
        for (var index = 0; index < preparedMessages.Length; index++)
        {
            PreparedHttpMessage? prepared = preparedMessages[index];
            if (prepared is null)
                continue;
            QueueData message = prepared.Message;
            CustomRequest customRequest = prepared.Request;
            logger.LogDebug(
                "{CustomRequestMethod}: {CustomRequestPath}{CustomRequestQuery} Sending",
                customRequest.Method,
                customRequest.Path,
                customRequest.Query);
            Stream? offloadedStream = prepared.OffloadedStream;

            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
            var configuration = new SlimFaasDefaultConfiguration
            {
                HttpTimeout = function.Configuration.DefaultAsync.HttpTimeout,
                TimeoutRetries = [],
                HttpStatusRetries = []
            };
            customRequest.Headers.Add(new CustomHeader(SlimfaasElementId, [message.Id]));
            customRequest.Headers.Add(new CustomHeader(
                SlimfaasLastTry,
                [message.IsLastTry.ToString().ToLowerInvariant()]));
            customRequest.Headers.Add(new CustomHeader(SlimfaasTryNumber, [message.TryNumber.ToString()]));
            string reservedIp = !string.IsNullOrWhiteSpace(message.ReservedIp)
                ? message.ReservedIp
                : index < reservedIps.Count ? reservedIps[index] : string.Empty;
            var requestCancellation = new CancellationTokenSource();
            Task<HttpResponseMessage> responseTask;
            try
            {
                responseTask = sendClient.SendHttpRequestAsync(
                    customRequest,
                    configuration,
                    null,
                    requestCancellation,
                    proxy,
                    reservedIp,
                    functionName,
                    null,
                    offloadedStream);
            }
            catch
            {
                requestCancellation.Dispose();
                if (offloadedStream is not null)
                    await offloadedStream.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var tracked = new TrackedHttpRequest(
                Volatile.Read(ref _trackingGeneration),
                functionName,
                customRequest,
                message.Id,
                reservedIp,
                offloadedStream,
                requestCancellation,
                function.Configuration.DefaultAsync.HttpStatusRetries.ToHashSet());
            active[message.Id] = tracked;
            _ = ObserveCompletionAsync(responseTask, tracked);
            activityTracker.Record(
                NetworkActivityTracker.EventTypes.Dequeue,
                NetworkActivityTracker.Actors.SlimFaas,
                functionName,
                functionName,
                targetPod: reservedIp);
        }
    }

    private async Task<PreparedHttpMessage?[]> PrepareMessagesAsync(
        string functionName,
        IList<QueueData> messages,
        CancellationToken cancellationToken)
    {
        var prepared = new PreparedHttpMessage?[messages.Count];
        var offloadedIndexes = new List<int>();
        for (var index = 0; index < messages.Count; index++)
        {
            QueueData message = messages[index];
            CustomRequest request;
            try
            {
                request = MemoryPackSerializer.Deserialize<CustomRequest>(message.Data);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Unable to deserialize async request. FunctionName={FunctionName} QueueElementId={QueueElementId}",
                    functionName,
                    message.Id);
                await slimFaasQueue.ListCallbackAsync(
                    functionName,
                    new ListQueueItemStatus
                    {
                        Items = [new QueueItemStatus(message.Id, StatusCodes.Status500InternalServerError)]
                    }).ConfigureAwait(false);
                continue;
            }

            prepared[index] = new PreparedHttpMessage(message, request, null);
            if (!string.IsNullOrEmpty(request.OffloadedFileId))
                offloadedIndexes.Add(index);
        }

        if (offloadedIndexes.Count == 0)
            return prepared;

        try
        {
            await Parallel.ForEachAsync(
                offloadedIndexes,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = MaximumConcurrentOffloadPreparations
                },
                async (index, token) =>
                {
                    PreparedHttpMessage message = prepared[index]!;
                    Stream? stream = await OpenOffloadedBodyAsync(
                        functionName,
                        message.Message.Id,
                        message.Request,
                        token).ConfigureAwait(false);
                    prepared[index] = stream is null
                        ? null
                        : message with { OffloadedStream = stream };
                }).ConfigureAwait(false);
        }
        catch
        {
            foreach (PreparedHttpMessage message in prepared.OfType<PreparedHttpMessage>())
            {
                if (message.OffloadedStream is not null)
                    await message.OffloadedStream.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }

        // Preparation may complete out of order, but dispatch below consumes this indexed
        // array in the original dequeue order and therefore preserves FIFO delivery order.
        return prepared;
    }

    private async Task<Stream?> OpenOffloadedBodyAsync(
        string functionName,
        string queueElementId,
        CustomRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.OffloadedFileId))
            return null;

        try
        {
            string metadataKey = DataFileKeys.MetaKey(request.OffloadedFileId);
            byte[]? metadataBytes = await db.GetAsync(metadataKey).ConfigureAwait(false);
            if (metadataBytes is null || metadataBytes.Length == 0)
            {
                await MarkOffloadedBodyUnavailableAsync(
                    functionName, queueElementId, request.OffloadedFileId, "metadata not found").ConfigureAwait(false);
                return null;
            }

            DataSetMetadata? metadata = MemoryPackSerializer.Deserialize<DataSetMetadata>(metadataBytes);
            if (metadata is null || string.IsNullOrWhiteSpace(metadata.Sha256Hex))
            {
                await MarkOffloadedBodyUnavailableAsync(
                    functionName, queueElementId, request.OffloadedFileId, "metadata invalid").ConfigureAwait(false);
                return null;
            }
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Loaded offloaded metadata. MetaKey={MetaKey} Tags={Tags}",
                    metadataKey,
                    FormatTags(metadata.Tags));
            }

            FilePullResult pulled = await fileSync.PullFileIfMissingAsync(
                request.OffloadedFileId,
                metadata.Sha256Hex,
                null,
                cancellationToken).ConfigureAwait(false);
            if (pulled.Stream is null)
            {
                await MarkOffloadedBodyUnavailableAsync(
                    functionName, queueElementId, request.OffloadedFileId, "file not found in cluster").ConfigureAwait(false);
            }
            return pulled.Stream;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to load offloaded body for id={FileId}. QueueElementId={QueueElementId}; reporting HTTP 500 to the queue",
                request.OffloadedFileId,
                queueElementId);
            await slimFaasQueue.ListCallbackAsync(
                functionName,
                new ListQueueItemStatus
                {
                    Items = [new QueueItemStatus(queueElementId, OffloadedBodyUnavailableStatusCode)]
                }).ConfigureAwait(false);
            return null;
        }
    }

    private async Task ObserveCompletionAsync(
        Task<HttpResponseMessage> responseTask,
        TrackedHttpRequest request)
    {
        var statusCode = 500;
        Exception? error = null;
        try
        {
            using HttpResponseMessage response = await responseTask.ConfigureAwait(false);
            statusCode = (int)response.StatusCode;
        }
        catch (Exception exception)
        {
            error = exception;
        }
        finally
        {
            try
            {
                if (request.OffloadedStream is not null)
                    await request.OffloadedStream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                request.Cancellation.Dispose();
            }
        }

        _completedRequests.Enqueue(new CompletedHttpRequest(request, statusCode, error));
        _queueSignal.Pulse();
    }

    private async Task DrainCompletedRequestsAsync(
        Dictionary<string, Dictionary<string, TrackedHttpRequest>> processingRequests,
        Dictionary<string, List<Awaiting202Request>> awaiting202Requests)
    {
        long generation = Volatile.Read(ref _trackingGeneration);
        var callbacks = new Dictionary<string, List<QueueItemStatus>>(StringComparer.Ordinal);
        var terminalOffloads = new List<string>();
        while (_completedRequests.TryDequeue(out CompletedHttpRequest completed))
        {
            TrackedHttpRequest request = completed.Request;
            if (processingRequests.TryGetValue(request.FunctionName, out var active))
                active.Remove(request.Id);
            if (request.Generation != generation)
                continue;

            historyHttpService.SetTickLastCall(request.FunctionName, DateTime.UtcNow.Ticks);
            if (completed.Error is not null)
            {
                logger.LogWarning(
                    completed.Error,
                    "Async request failed for {FunctionName}/{ElementId}",
                    request.FunctionName,
                    request.Id);
            }
            else
            {
                logger.LogDebug(
                    "{CustomRequestMethod}: /async-function{CustomRequestPath}{CustomRequestQuery} {StatusCode}",
                    request.CustomRequest.Method,
                    request.CustomRequest.Path,
                    request.CustomRequest.Query,
                    completed.StatusCode);
            }

            if (completed.StatusCode == StatusCodes.Status202Accepted && completed.Error is null)
            {
                GetOrAdd(awaiting202Requests, request.FunctionName)
                    .Add(new Awaiting202Request(request.Id, request.TargetIp));
                continue;
            }

            activityTracker.Record(
                NetworkActivityTracker.EventTypes.RequestEnd,
                request.FunctionName,
                NetworkActivityTracker.Actors.SlimFaas,
                request.FunctionName,
                targetPod: request.TargetIp);
            if (!callbacks.TryGetValue(request.FunctionName, out List<QueueItemStatus>? statuses))
            {
                statuses = [];
                callbacks.Add(request.FunctionName, statuses);
            }
            statuses.Add(new QueueItemStatus(request.Id, completed.StatusCode));
            if (!string.IsNullOrEmpty(request.CustomRequest.OffloadedFileId) &&
                !request.HttpStatusRetries.Contains(completed.StatusCode))
            {
                terminalOffloads.Add(request.CustomRequest.OffloadedFileId);
            }
        }

        foreach ((string functionName, List<QueueItemStatus> statuses) in callbacks)
        {
            await slimFaasQueue.ListCallbackAsync(
                functionName,
                new ListQueueItemStatus { Items = statuses }).ConfigureAwait(false);
        }

        if (terminalOffloads.Count > 0)
        {
            foreach (string fileId in terminalOffloads.Distinct(StringComparer.Ordinal))
                _terminalOffloadCleanup.Writer.TryWrite(fileId);
        }
    }

    private async Task RunTerminalOffloadCleanupAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (string first in _terminalOffloadCleanup.Reader.ReadAllAsync(stoppingToken))
            {
                var fileIds = new HashSet<string>(StringComparer.Ordinal) { first };
                while (fileIds.Count < MaximumOffloadCleanupBatchSize &&
                       _terminalOffloadCleanup.Reader.TryRead(out string? fileId))
                {
                    fileIds.Add(fileId);
                }
                await Task.WhenAll(fileIds.Select(CleanupTerminalOffloadAsync)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CleanupTerminalOffloadAsync(string fileId)
    {
        try
        {
            // The callback was committed first, so this dispatching replica can release its
            // local file without racing a retry. Raft metadata and remote copies are left to
            // the convergent orphan cleaner; deleting them here would put cleanup mutations
            // ahead of new queue writes in the strict global FIFO.
            await fileSync.BroadcastFileDeleteAsync(fileId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Unable to clean terminal async offload. FileId={FileId}",
                fileId);
        }
    }

    private void CompleteAwaiting202(
        string functionName,
        List<Awaiting202Request> pending,
        IReadOnlyList<QueueRunningReservation> runningReservations)
    {
        if (pending.Count == 0)
            return;
        var runningIds = runningReservations
            .Select(reservation => reservation.Id)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = pending.Count - 1; index >= 0; index--)
        {
            Awaiting202Request item = pending[index];
            if (runningIds.Contains(item.Id))
                continue;
            pending.RemoveAt(index);
            activityTracker.Record(
                NetworkActivityTracker.EventTypes.RequestEnd,
                functionName,
                NetworkActivityTracker.Actors.SlimFaas,
                functionName,
                targetPod: item.TargetIp);
        }
    }

    private void UpdateLastCallIfWorkRemains(
        int functionReplicas,
        Dictionary<string, int> counters,
        string functionName,
        int activeRequests,
        int queueLength)
    {
        int counterLimit = functionReplicas == 0 ? 10 : 40;
        counters[functionName]++;
        if (counters[functionName] <= counterLimit)
            return;
        counters[functionName] = 0;
        if (queueLength > 0 || activeRequests > 0)
            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
    }

    private async Task MarkOffloadedBodyUnavailableAsync(
        string functionName,
        string queueElementId,
        string fileId,
        string reason)
    {
        logger.LogWarning(
            "Unable to load offloaded body for id={FileId}: {Reason}. QueueElementId={QueueElementId}; reporting HTTP 500 to the queue",
            fileId,
            reason,
            queueElementId);
        await slimFaasQueue.ListCallbackAsync(
            functionName,
            new ListQueueItemStatus
            {
                Items = [new QueueItemStatus(queueElementId, OffloadedBodyUnavailableStatusCode)]
            }).ConfigureAwait(false);
    }

    private static object? FormatTags(IDictionary<string, string>? tags) =>
        tags is null || tags.Count == 0
            ? null
            : string.Join(", ", tags.Select(pair => $"{pair.Key}={pair.Value}"));

    private void ClearLocalTracking(
        Dictionary<string, Dictionary<string, TrackedHttpRequest>> processingRequests,
        Dictionary<string, List<Awaiting202Request>> awaiting202Requests)
    {
        if (processingRequests.Values.All(active => active.Count == 0) &&
            awaiting202Requests.Values.All(pending => pending.Count == 0))
        {
            return;
        }

        Interlocked.Increment(ref _trackingGeneration);
        foreach (Dictionary<string, TrackedHttpRequest> active in processingRequests.Values)
        {
            foreach (TrackedHttpRequest request in active.Values)
            {
                try
                {
                    request.Cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
                try
                {
                    request.OffloadedStream?.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }
            active.Clear();
        }
        foreach (List<Awaiting202Request> pending in awaiting202Requests.Values)
            pending.Clear();
    }

    private static TValue GetOrAdd<TValue>(
        IDictionary<string, TValue> dictionary,
        string key)
        where TValue : new()
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            value = new TValue();
            dictionary.Add(key, value);
        }
        return value;
    }
}
