﻿using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

internal record struct RequestToWait(Task<HttpResponseMessage> Task, CustomRequest CustomRequest, string Id);

public class SlimQueuesWorker(
    ISlimFaasQueue slimFaasQueue,
    IReplicasService replicasService,
    HistoryHttpMemoryService historyHttpService,
    ILogger<SlimQueuesWorker> logger,
    IServiceProvider serviceProvider,
    ISlimDataStatus slimDataStatus,
    IMasterService masterService,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{

    public const string SlimfaasElementId = "SlimFaas-Element-Id";
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
            customRequest.Headers.Add(new CustomHeader(SlimfaasElementId, [requestJson.Id]));
            Task<HttpResponseMessage> taskResponse = scope.ServiceProvider.GetRequiredService<ISendClient>()
                .SendHttpRequestAsync(customRequest, slimfaasDefaultConfiguration, null, null, new Proxy(replicasService, functionDeployment));
            processingTasks[functionDeployment].Add(new RequestToWait(taskResponse, customRequest, requestJson.Id));
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
                if (statusCode == 202)
                {
                    logger.LogInformation("SlimFaas is waiting callback from {FunctionDeployment}", functionDeployment);
                }
                else
                {
                    queueItemStatusList.Add(new QueueItemStatus(processing.Id, statusCode));
                }
                httpResponseMessage.Dispose();
            }
            catch (Exception e)
            {
                queueItemStatusList.Add(new QueueItemStatus(processing.Id, 500));
                httpResponseMessagesToDelete.Add(processing);
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
