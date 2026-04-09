using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
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
    bool AwaitingCallback = false,
    DateTime? CallbackDeadline = null);

public class SlimQueuesWorker(
    ISlimFaasQueue slimFaasQueue,
    IReplicasService replicasService,
    HistoryHttpMemoryService historyHttpService,
    ILogger<SlimQueuesWorker> logger,
    IServiceProvider serviceProvider,
    ISlimDataStatus slimDataStatus,
    IMasterService masterService,
    IOptions<WorkersOptions> workersOptions,
    NetworkActivityTracker activityTracker,
    CallbackCompletionTracker callbackTracker)
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
        Dictionary<string, int> setTickLastCallCounterDictionary = new();
        while (stoppingToken.IsCancellationRequested == false)
        {
            await DoOneCycle(stoppingToken, setTickLastCallCounterDictionary, processingTasks);
        }
    }

    private async Task DoOneCycle(CancellationToken stoppingToken,
        Dictionary<string, int> setTickLastCallCounterDictionary,
        Dictionary<string, IList<RequestToWait>> processingTasks)
    {
        try
        {
            await Task.Delay(_delay, stoppingToken);
            if (!masterService.IsMaster)
            {
                return;
            }
            DeploymentsInformations deployments = replicasService.Deployments;
            IList<DeploymentInformation> functions = deployments.Functions;
            foreach (DeploymentInformation function in functions)
            {
                string functionDeployment = function.Deployment;
                setTickLastCallCounterDictionary.TryAdd(functionDeployment, 0);
                int numberProcessingTasks = await ManageProcessingTasksAsync(slimFaasQueue, processingTasks, functionDeployment);

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
        var jsons = await slimFaasQueue.DequeueAsync(functionDeployment, numberLimitProcessingTasks);

        if (jsons == null)
        {
            return;
        }

        var proxy = new Proxy(replicasService, functionDeployment);

        foreach (var requestJson in jsons)
        {
            CustomRequest customRequest = MemoryPackSerializer.Deserialize<CustomRequest>(requestJson.Data);

            logger.LogDebug("{CustomRequestMethod}: {CustomRequestPath}{CustomRequestQuery} Sending",
                customRequest.Method, customRequest.Path, customRequest.Query);
            logger.LogDebug("{RequestJson}", requestJson);
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

            // Sélectionner le pod en respectant la limite per-pod
            string targetIp = proxy.GetNextIP(function.NumberParallelRequestPerPod);
            if (string.IsNullOrEmpty(targetIp))
            {
                // Tous les pods sont saturés — remettre le message en queue sera géré au prochain cycle
                logger.LogDebug("All pods saturated for {FunctionDeployment}, skipping remaining requests", functionDeployment);
                break;
            }

            Task<HttpResponseMessage> taskResponse = scope.ServiceProvider.GetRequiredService<ISendClient>()
                .SendHttpRequestAsync(customRequest, slimfaasDefaultConfiguration, null, null, proxy);
            processingTasks[functionDeployment].Add(new RequestToWait(taskResponse, customRequest, requestJson.Id, targetIp));
            activityTracker.Record("dequeue", "slimfaas", functionDeployment, functionDeployment, targetPod: targetIp);
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
        string functionDeployment)
    {
        if (processingTasks.ContainsKey(functionDeployment) == false)
        {
            processingTasks.Add(functionDeployment, new List<RequestToWait>());
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

                // ── Callback-awaiting entries (202 returned, waiting for callback or timeout) ──
                if (processing.AwaitingCallback)
                {
                    // Check if the callback has arrived via the tracker
                    if (callbackTracker.TryConsumeCompletion(processing.Id, out int callbackStatus))
                    {
                        logger.LogInformation(
                            "Callback received for {FunctionDeployment} element {ElementId} with status {Status}",
                            functionDeployment, processing.Id, callbackStatus);
                        httpResponseMessagesToDelete.Add(processing);
                        activityTracker.Record("request_end", functionDeployment, "slimfaas", functionDeployment,
                            targetPod: processing.TargetIp);
                        // The callback already resolved the element via ListCallbackAsync,
                        // no need to add to queueItemStatusList again.
                    }
                    // Check if the deadline has been exceeded (timeout, no callback received)
                    else if (processing.CallbackDeadline.HasValue && DateTime.UtcNow >= processing.CallbackDeadline.Value)
                    {
                        logger.LogWarning(
                            "Callback timeout for {FunctionDeployment} element {ElementId}, releasing resources",
                            functionDeployment, processing.Id);
                        httpResponseMessagesToDelete.Add(processing);
                        activityTracker.Record("request_end", functionDeployment, "slimfaas", functionDeployment,
                            targetPod: processing.TargetIp);
                        // Mark as failed so the queue can retry
                        queueItemStatusList.Add(new QueueItemStatus(processing.Id, 504));
                    }
                    // Otherwise keep waiting — the callback will arrive or we'll timeout next cycle
                    continue;
                }

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

                if (statusCode == 202)
                {
                    // The function accepted the request and will call back.
                    // Keep the Proxy slot occupied and do NOT emit request_end yet.
                    // Compute a deadline based on the configured async timeout.
                    var function = replicasService.Deployments.Functions
                        .FirstOrDefault(f => f.Deployment == functionDeployment);
                    int timeoutSeconds = function?.Configuration.DefaultAsync.HttpTimeout ?? 120;
                    var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

                    logger.LogInformation(
                        "SlimFaas is waiting callback from {FunctionDeployment}, deadline {Deadline}",
                        functionDeployment, deadline);

                    // Replace the entry with an awaiting-callback version
                    // (records are immutable, so we remove + re-add)
                    httpResponseMessagesToDelete.Add(processing);
                    requestToWaits.Add(new RequestToWait(
                        Task.FromResult(httpResponseMessage),
                        processing.CustomRequest,
                        processing.Id,
                        processing.TargetIp,
                        AwaitingCallback: true,
                        CallbackDeadline: deadline));
                }
                else
                {
                    httpResponseMessagesToDelete.Add(processing);
                    activityTracker.Record("request_end", functionDeployment, "slimfaas", functionDeployment,
                        targetPod: processing.TargetIp);
                    queueItemStatusList.Add(new QueueItemStatus(processing.Id, statusCode));
                    httpResponseMessage.Dispose();
                }
            }
            catch (Exception e)
            {
                queueItemStatusList.Add(new QueueItemStatus(processing.Id, 500));
                httpResponseMessagesToDelete.Add(processing);
                activityTracker.Record("request_end", functionDeployment, "slimfaas", functionDeployment, targetPod: processing.TargetIp);
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

}
