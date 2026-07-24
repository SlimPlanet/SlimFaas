using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.ClusterFiles;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

internal record struct RequestToWait(
    Task<HttpResponseMessage> Task,
    CustomRequest CustomRequest,
    string Id,
    string TargetIp,
    Stream? OffloadedStream,
    CancellationTokenSource Cancellation);

internal readonly record struct Awaiting202Request(string Id, string TargetIp);

public class SlimQueuesWorker(
    ISlimFaasQueue slimFaasQueue,
    IReplicasService replicasService,
    HistoryHttpMemoryService historyHttpService,
    ILogger<SlimQueuesWorker> logger,
    IServiceProvider serviceProvider,
    ISlimDataStatus slimDataStatus,
    IMasterService masterService,
    IClusterFileSync fileSync,
    IDatabaseService db,
    IOptions<WorkersOptions> workersOptions,
    NetworkActivityTracker activityTracker)
    : BackgroundService
{

    public const string SlimfaasElementId = "SlimFaas-Element-Id";
    public const string SlimfaasTryNumber = "SlimFaas-Try-Number";
    public const string SlimfaasLastTry = "Slimfaas-Last-Try";
    private const int OffloadedBodyUnavailableStatusCode = 500;
    private readonly int _delay = workersOptions.Value.QueuesDelayMilliseconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await slimDataStatus.WaitForReadyAsync();
        Dictionary<string, IList<RequestToWait>> processingTasks = new();
        Dictionary<string, IList<Awaiting202Request>> awaiting202Tasks = new();
        Dictionary<string, int> setTickLastCallCounterDictionary = new();
        try
        {
            while (stoppingToken.IsCancellationRequested == false)
            {
                await DoOneCycle(stoppingToken, setTickLastCallCounterDictionary, processingTasks, awaiting202Tasks);
            }
        }
        finally
        {
            ClearLocalTracking(processingTasks, awaiting202Tasks);
        }
    }

    private async Task DoOneCycle(CancellationToken stoppingToken,
        Dictionary<string, int> setTickLastCallCounterDictionary,
        Dictionary<string, IList<RequestToWait>> processingTasks,
        Dictionary<string, IList<Awaiting202Request>> awaiting202Tasks)
    {
        try
        {
            await Task.Delay(_delay, stoppingToken);
            if (!masterService.IsMaster)
            {
                ClearLocalTracking(processingTasks, awaiting202Tasks);
                return;
            }
            DeploymentsInformations deployments = replicasService.Deployments;
            IList<DeploymentInformation> functions = deployments.Functions;
            foreach (DeploymentInformation function in functions)
            {
                string functionDeployment = function.Deployment;
                setTickLastCallCounterDictionary.TryAdd(functionDeployment, 0);
                await ManageAwaiting202TasksAsync(awaiting202Tasks, functionDeployment);
                int numberProcessingTasks = await ManageProcessingTasksAsync(slimFaasQueue, processingTasks, awaiting202Tasks, functionDeployment);

                var numberPodsReady = function.Pods?.Count(p  => p.Ready.HasValue && p.Ready.Value && !string.IsNullOrEmpty(p.Ip)) ?? 1;

                int numberMaxProcessingTasks = Math.Min(function.NumberParallelRequest, numberPodsReady * function.NumberParallelRequestPerPod);
                int numberLimitProcessingTasks = numberMaxProcessingTasks - numberProcessingTasks;
                setTickLastCallCounterDictionary[functionDeployment]++;
                int functionReplicas = function.Replicas;
                long queueLength = await UpdateTickLastCallIfRequestStillInProgress(
                    functionReplicas,
                    setTickLastCallCounterDictionary,
                    functionDeployment,
                    numberProcessingTasks,
                    numberLimitProcessingTasks);

                if (functionReplicas == 0 || queueLength <= 0)
                {
                    continue;
                }

                bool? isAnyContainerStarted = function.Pods?.Any(p => p.Ready.HasValue && p.Ready.Value);
                if (!isAnyContainerStarted.HasValue || !isAnyContainerStarted.Value || !function.EndpointReady)
                {
                    continue;
                }

                if (numberProcessingTasks >= numberMaxProcessingTasks)
                {
                    continue;
                }

                await SendHttpRequestToFunction(processingTasks, numberLimitProcessingTasks,
                    function);
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Global Error in SlimFaas Worker");
        }
    }

    private async Task SendHttpRequestToFunction(Dictionary<string, IList<RequestToWait>> processingTasks,
        int numberLimitProcessingTasks,
        DeploymentInformation function)
    {
        string functionDeployment = function.Deployment;
        var proxy = new Proxy(replicasService, functionDeployment);

        // Récupère l'état des éléments "Running" depuis la base : leurs IPs réservées
        // représentent la charge actuelle par pod (source de vérité partagée).
        var runningElements = await slimFaasQueue.ListElementsAsync(functionDeployment, [CountType.Running]);
        var alreadyUsedIps = runningElements
            .Select(r => r.ReservedIp)
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .ToList();

        var reservedIps = proxy.ReserveNextIPs(function.NumberParallelRequestPerPod, numberLimitProcessingTasks, alreadyUsedIps);
        if (reservedIps.Count == 0)
        {
            logger.LogDebug("All pods saturated for {FunctionDeployment}, skipping dequeue", functionDeployment);
            return;
        }

        var jsons = await slimFaasQueue.DequeueAsync(functionDeployment, reservedIps.Count, reservedIps);

        if (jsons == null)
        {
            return;
        }

        for (var i = 0; i < jsons.Count; i++)
        {
            var requestJson = jsons[i];
            CustomRequest customRequest = MemoryPackSerializer.Deserialize<CustomRequest>(requestJson.Data);

            logger.LogDebug("{CustomRequestMethod}: {CustomRequestPath}{CustomRequestQuery} Sending",
                customRequest.Method, customRequest.Path, customRequest.Query);

            Stream? offloadedStream = null;
            if (!string.IsNullOrEmpty(customRequest.OffloadedFileId))
            {
                try
                {
                    var metaKey = DataFileKeys.MetaKey(customRequest.OffloadedFileId);
                    var metaBytes = await db.GetAsync(metaKey);
                    if (metaBytes is null || metaBytes.Length == 0)
                    {
                        await MarkOffloadedBodyUnavailableAsync(
                            functionDeployment,
                            requestJson.Id,
                            customRequest.OffloadedFileId,
                            "metadata not found");
                        continue;
                    }

                    var meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(metaBytes);
                    if (meta is null || string.IsNullOrWhiteSpace(meta.Sha256Hex))
                    {
                        await MarkOffloadedBodyUnavailableAsync(
                            functionDeployment,
                            requestJson.Id,
                            customRequest.OffloadedFileId,
                            "metadata invalid");
                        continue;
                    }

                    if(logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "Loaded offloaded metadata. MetaKey={MetaKey} Tags={Tags}",
                            metaKey,
                            FormatTags(meta.Tags));
                    }

                    var pulled = await fileSync.PullFileIfMissingAsync(
                        customRequest.OffloadedFileId,
                        meta.Sha256Hex,
                        null,
                        CancellationToken.None);
                    offloadedStream = pulled.Stream;
                    if (offloadedStream is null)
                    {
                        await MarkOffloadedBodyUnavailableAsync(
                            functionDeployment,
                            requestJson.Id,
                            customRequest.OffloadedFileId,
                            "file not found in cluster");
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(
                        ex,
                        "Unable to load offloaded body for id={FileId}. QueueElementId={QueueElementId}; reporting HTTP 500 to the queue",
                        customRequest.OffloadedFileId,
                        requestJson.Id);
                    await slimFaasQueue.ListCallbackAsync(
                        functionDeployment,
                        new ListQueueItemStatus
                        {
                            Items = [new QueueItemStatus(requestJson.Id, OffloadedBodyUnavailableStatusCode)]
                        });
                    continue;
                }
            }
            else
            {
                logger.LogDebug("{RequestJson}", requestJson);
            }

            historyHttpService.SetTickLastCall(functionDeployment, DateTime.UtcNow.Ticks);
            using IServiceScope scope = serviceProvider.CreateScope();
            var slimfaasDefaultConfiguration = new SlimFaasDefaultConfiguration()
            {
                HttpTimeout = function.Configuration.DefaultAsync.HttpTimeout,
                TimeoutRetries = [],
                HttpStatusRetries = []
            };
            List<CustomHeader> customRequestHeaders = customRequest.Headers;
            customRequestHeaders.Add(new CustomHeader(SlimfaasElementId, [requestJson.Id]));
            customRequestHeaders.Add(new CustomHeader(SlimfaasLastTry, [requestJson.IsLastTry.ToString().ToLowerInvariant()]));
            customRequestHeaders.Add(new CustomHeader(SlimfaasTryNumber, [requestJson.TryNumber.ToString()]));
            var reservedIp = !string.IsNullOrWhiteSpace(requestJson.ReservedIp)
                ? requestJson.ReservedIp
                : (i < reservedIps.Count ? reservedIps[i] : string.Empty);

            string targetIp = reservedIp;

            var requestCancellation = new CancellationTokenSource();
            Task<HttpResponseMessage> taskResponse;
            try
            {
                taskResponse = scope.ServiceProvider.GetRequiredService<ISendClient>()
                    .SendHttpRequestAsync(
                        customRequest,
                        slimfaasDefaultConfiguration,
                        null,
                        requestCancellation,
                        proxy,
                        reservedIp,
                        functionDeployment,
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

            processingTasks[functionDeployment].Add(
                new RequestToWait(
                    taskResponse,
                    customRequest,
                    requestJson.Id,
                    targetIp,
                    offloadedStream,
                    requestCancellation));
            activityTracker.Record(NetworkActivityTracker.EventTypes.Dequeue, NetworkActivityTracker.Actors.SlimFaas, functionDeployment, functionDeployment, targetPod: targetIp);
        }
    }

    private async Task MarkOffloadedBodyUnavailableAsync(
        string functionDeployment,
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
            functionDeployment,
            new ListQueueItemStatus
            {
                Items = [new QueueItemStatus(queueElementId, OffloadedBodyUnavailableStatusCode)]
            });
    }

    private object? FormatTags(IDictionary<string, string>? metaTags)
    {
        if (metaTags == null || metaTags.Count == 0)
            return null;

        return string.Join(", ", metaTags.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private async Task<long> UpdateTickLastCallIfRequestStillInProgress(int? functionReplicas,
        Dictionary<string, int> setTickLastCallCounterDictionnary,
        string functionDeployment,
        int numberProcessingTasks,
        int numberLimitProcessingTasks)
    {
            int counterLimit = functionReplicas == 0 ? 10 : 40;
            long queueLength = await slimFaasQueue.CountElementAsync(functionDeployment, new List<CountType>()
            {
                CountType.Available,
                CountType.Running,
                CountType.WaitingForRetry
            } );
            if (setTickLastCallCounterDictionnary[functionDeployment] > counterLimit)
            {
                setTickLastCallCounterDictionnary[functionDeployment] = 0;

                if (queueLength > 0 || numberProcessingTasks > 0)
                {
                    historyHttpService.SetTickLastCall(functionDeployment, DateTime.UtcNow.Ticks);
                }
            }

            if (queueLength == 0)
            {
                return 0;
            }

            return await slimFaasQueue.CountElementAsync(functionDeployment,  new List<CountType>() { CountType.Available }, numberLimitProcessingTasks);
    }

    private async Task<int> ManageProcessingTasksAsync(ISlimFaasQueue slimFaasQueue,
        Dictionary<string, IList<RequestToWait>> processingTasks,
        Dictionary<string, IList<Awaiting202Request>> awaiting202Tasks,
        string functionDeployment)
    {
        if (processingTasks.ContainsKey(functionDeployment) == false)
        {
            processingTasks.Add(functionDeployment, new List<RequestToWait>());
        }
        if (awaiting202Tasks.ContainsKey(functionDeployment) == false)
        {
            awaiting202Tasks.Add(functionDeployment, new List<Awaiting202Request>());
        }
        var listQueueItemStatus = new ListQueueItemStatus();
        var queueItemStatusList = new List<QueueItemStatus>();
        listQueueItemStatus.Items = queueItemStatusList;
        List<RequestToWait> completedRequests = new();
        IList<RequestToWait> requestToWaits = processingTasks[functionDeployment];
        foreach (RequestToWait processing in requestToWaits)
        {
            if (!processing.Task.IsCompleted)
            {
                continue;
            }

            try
            {
                historyHttpService.SetTickLastCall(functionDeployment, DateTime.UtcNow.Ticks);

                using HttpResponseMessage httpResponseMessage = await processing.Task;
                var statusCode = (int)httpResponseMessage.StatusCode;
                logger.LogDebug(
                    "{CustomRequestMethod}: /async-function{CustomRequestPath}{CustomRequestQuery} {StatusCode}",
                    processing.CustomRequest.Method, processing.CustomRequest.Path, processing.CustomRequest.Query,
                    httpResponseMessage.StatusCode);

                if (statusCode == 202)
                {
                    awaiting202Tasks[functionDeployment].Add(
                        new Awaiting202Request(processing.Id, processing.TargetIp));
                }
                else
                {
                    activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, functionDeployment, NetworkActivityTracker.Actors.SlimFaas, functionDeployment,
                        targetPod: processing.TargetIp);
                    queueItemStatusList.Add(new QueueItemStatus(processing.Id, statusCode));
                }
            }
            catch (Exception e)
            {
                queueItemStatusList.Add(new QueueItemStatus(processing.Id, 500));
                activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, functionDeployment, NetworkActivityTracker.Actors.SlimFaas, functionDeployment, targetPod: processing.TargetIp);
                logger.LogWarning("Request Error: {Message} {StackTrace}", e.Message, e.StackTrace);
            }
            finally
            {
                completedRequests.Add(processing);
                await DisposeRequestResourcesAsync(processing).ConfigureAwait(false);
            }
        }

        if (listQueueItemStatus.Items.Count > 0)
        {
            await slimFaasQueue.ListCallbackAsync(functionDeployment, listQueueItemStatus);
        }

        foreach (RequestToWait completed in completedRequests)
        {
            requestToWaits.Remove(completed);
        }

        int numberProcessingTasks = requestToWaits.Count;
        return numberProcessingTasks;
    }

    private async Task ManageAwaiting202TasksAsync(
        Dictionary<string, IList<Awaiting202Request>> awaiting202Tasks,
        string functionDeployment)
    {
        if (!awaiting202Tasks.ContainsKey(functionDeployment))
        {
            awaiting202Tasks[functionDeployment] = new List<Awaiting202Request>();
        }

        IList<Awaiting202Request> pending = awaiting202Tasks[functionDeployment];
        if (pending.Count == 0)
        {
            return;
        }

        var running = await slimFaasQueue.ListElementsAsync(functionDeployment, [CountType.Running]);
        var runningByElementId = running.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        List<Awaiting202Request> completed = new();

        foreach (var item in pending)
        {
            if (runningByElementId.Contains(item.Id))
            {
                continue;
            }

            completed.Add(item);
            activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, functionDeployment, NetworkActivityTracker.Actors.SlimFaas, functionDeployment,
                targetPod: item.TargetIp);
        }

        foreach (var item in completed)
        {
            pending.Remove(item);
        }
    }

    private void ClearLocalTracking(
        Dictionary<string, IList<RequestToWait>> processingTasks,
        Dictionary<string, IList<Awaiting202Request>> awaiting202Tasks)
    {
        foreach (var kv in processingTasks)
        {
            foreach (var request in kv.Value)
            {
                try
                {
                    request.Cancellation.Cancel();
                }
                catch
                {
                    // Best effort during shutdown or a leadership transition.
                }

                try
                {
                    request.OffloadedStream?.Dispose();
                }
                catch
                {
                    // Continue releasing the remaining resources.
                }
                request.Cancellation.Dispose();
                _ = ObserveAndDisposeResponseAsync(request.Task);
            }

            kv.Value.Clear();
        }

        foreach (var kv in awaiting202Tasks)
        {

            kv.Value.Clear();
        }
    }

    private static async Task DisposeRequestResourcesAsync(RequestToWait request)
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

    private static async Task ObserveAndDisposeResponseAsync(Task<HttpResponseMessage> responseTask)
    {
        try
        {
            using var response = await responseTask.ConfigureAwait(false);
        }
        catch
        {
            // Expected after cancellation on shutdown or leadership loss.
        }
    }

}
