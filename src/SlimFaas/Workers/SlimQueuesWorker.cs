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
    Stream? OffloadedStream = null);

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
    private readonly int _delay = workersOptions.Value.QueuesDelayMilliseconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await slimDataStatus.WaitForReadyAsync();
        Dictionary<string, IList<RequestToWait>> processingTasks = new();
        Dictionary<string, IList<RequestToWait>> awaiting202Tasks = new();
        Dictionary<string, int> setTickLastCallCounterDictionary = new();
        while (stoppingToken.IsCancellationRequested == false)
        {
            await DoOneCycle(stoppingToken, setTickLastCallCounterDictionary, processingTasks, awaiting202Tasks);
        }
    }

    private async Task DoOneCycle(CancellationToken stoppingToken,
        Dictionary<string, int> setTickLastCallCounterDictionary,
        Dictionary<string, IList<RequestToWait>> processingTasks,
        Dictionary<string, IList<RequestToWait>> awaiting202Tasks)
    {
        try
        {
            await Task.Delay(_delay, stoppingToken);
            if (!masterService.IsMaster)
            {
                ClearLocalTrackingOnLeadershipLoss(processingTasks, awaiting202Tasks);
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
                var metaKey = $"data:file:{customRequest.OffloadedFileId}:meta";
                var metaBytes = await db.GetAsync(metaKey);
                if (metaBytes != null && metaBytes.Length > 0)
                {
                    var meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(metaBytes);
                    var pulled = await fileSync.PullFileIfMissingAsync(
                        customRequest.OffloadedFileId,
                        meta?.Sha256Hex ?? "",
                        null,
                        CancellationToken.None);
                    offloadedStream = pulled.Stream;
                }
                else
                {
                    logger.LogWarning("Offloaded file metadata not found for id={FileId}, request will have empty body",
                        customRequest.OffloadedFileId);
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

            Task<HttpResponseMessage> taskResponse = scope.ServiceProvider.GetRequiredService<ISendClient>()
                .SendHttpRequestAsync(customRequest, slimfaasDefaultConfiguration, null, null, proxy, reservedIp, functionDeployment, null, offloadedStream);
            processingTasks[functionDeployment].Add(new RequestToWait(taskResponse, customRequest, requestJson.Id, targetIp, offloadedStream));
            activityTracker.Record(NetworkActivityTracker.EventTypes.Dequeue, NetworkActivityTracker.Actors.SlimFaas, functionDeployment, functionDeployment, targetPod: targetIp);
        }
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
        Dictionary<string, IList<RequestToWait>> awaiting202Tasks,
        string functionDeployment)
    {
        if (processingTasks.ContainsKey(functionDeployment) == false)
        {
            processingTasks.Add(functionDeployment, new List<RequestToWait>());
        }
        if (awaiting202Tasks.ContainsKey(functionDeployment) == false)
        {
            awaiting202Tasks.Add(functionDeployment, new List<RequestToWait>());
        }
        var listQueueItemStatus = new ListQueueItemStatus();
        var queueItemStatusList = new List<QueueItemStatus>();
        listQueueItemStatus.Items = queueItemStatusList;
        List<RequestToWait> httpResponseMessagesToDelete = new();
        IList<RequestToWait> requestToWaits = processingTasks[functionDeployment];
        foreach (RequestToWait processing in requestToWaits)
        {
            try
            {
                historyHttpService.SetTickLastCall(functionDeployment, DateTime.UtcNow.Ticks);

                if (!processing.Task.IsCompleted)
                {
                    continue;
                }

                HttpResponseMessage httpResponseMessage = await processing.Task;
                var statusCode = (int)httpResponseMessage.StatusCode;
                logger.LogDebug(
                    "{CustomRequestMethod}: /async-function{CustomRequestPath}{CustomRequestQuery} {StatusCode}",
                    processing.CustomRequest.Method, processing.CustomRequest.Path, processing.CustomRequest.Query,
                    httpResponseMessage.StatusCode);
                httpResponseMessagesToDelete.Add(processing);
                await CleanOffloadedStream(processing);

                if (statusCode == 202)
                {
                    httpResponseMessage.Dispose();
                    httpResponseMessagesToDelete.Add(processing);
                    awaiting202Tasks[functionDeployment].Add(processing);
                }
                else
                {
                    httpResponseMessagesToDelete.Add(processing);
                    activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, functionDeployment, NetworkActivityTracker.Actors.SlimFaas, functionDeployment,
                        targetPod: processing.TargetIp);
                    queueItemStatusList.Add(new QueueItemStatus(processing.Id, statusCode));
                    httpResponseMessage.Dispose();
                }
            }
            catch (Exception e)
            {
                queueItemStatusList.Add(new QueueItemStatus(processing.Id, 500));
                httpResponseMessagesToDelete.Add(processing);
                activityTracker.Record(NetworkActivityTracker.EventTypes.RequestEnd, functionDeployment, NetworkActivityTracker.Actors.SlimFaas, functionDeployment, targetPod: processing.TargetIp);
                await CleanOffloadedStream(processing);
                logger.LogWarning("Request Error: {Message} {StackTrace}", e.Message, e.StackTrace);
            }
        }

        if (listQueueItemStatus.Items.Count > 0)
        {
            await slimFaasQueue.ListCallbackAsync(functionDeployment, listQueueItemStatus);
        }

        foreach (RequestToWait httpResponseMessage in httpResponseMessagesToDelete)
        {
            requestToWaits.Remove(httpResponseMessage);
        }

        int numberProcessingTasks = requestToWaits.Count;
        return numberProcessingTasks;
    }

    private async Task ManageAwaiting202TasksAsync(
        Dictionary<string, IList<RequestToWait>> awaiting202Tasks,
        string functionDeployment)
    {
        if (!awaiting202Tasks.ContainsKey(functionDeployment))
        {
            awaiting202Tasks[functionDeployment] = new List<RequestToWait>();
        }

        IList<RequestToWait> pending = awaiting202Tasks[functionDeployment];
        if (pending.Count == 0)
        {
            return;
        }

        var running = await slimFaasQueue.ListElementsAsync(functionDeployment, [CountType.Running]);
        var runningByElementId = running.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        List<RequestToWait> completed = new();

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

    private async Task ManageAllAwaiting202TasksAsync(
        Dictionary<string, IList<RequestToWait>> awaiting202Tasks)
    {
        foreach (var functionDeployment in awaiting202Tasks.Keys.ToList())
        {
            await ManageAwaiting202TasksAsync(awaiting202Tasks, functionDeployment);
        }
    }

    private void ClearLocalTrackingOnLeadershipLoss(
        Dictionary<string, IList<RequestToWait>> processingTasks,
        Dictionary<string, IList<RequestToWait>> awaiting202Tasks)
    {
        foreach (var kv in processingTasks)
        {
            kv.Value.Clear();
        }

        foreach (var kv in awaiting202Tasks)
        {

            kv.Value.Clear();
        }
    }

    private async Task CleanOffloadedStream(RequestToWait processing)
    {
        if (processing.OffloadedStream != null)
        {
            await processing.OffloadedStream.DisposeAsync();
        }
        if (!string.IsNullOrEmpty(processing.CustomRequest.OffloadedFileId))
        {
            string metaKey = $"data:file:{processing.CustomRequest.OffloadedFileId}:meta";
            await db.DeleteAsync(metaKey);
        }
    }
}
