using MemoryPack;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using Microsoft.Extensions.Options;

namespace SlimFaas.WebSocket;

internal record struct WebSocketRequestToWait(Task<int> Task, string FunctionName, string Id);

/// <summary>
/// Worker qui poll les queues des fonctions WebSocket virtuelles
/// et dispatch les messages via WebSocket au lieu d'HTTP.
/// Fonctionne comme <see cref="SlimQueuesWorker"/> : les tâches en cours sont
/// trackées entre les cycles et libérées au fur et à mesure.
/// </summary>
public class WebSocketQueuesWorker(
    ISlimFaasQueue slimFaasQueue,
    WebSocketConnectionRegistry registry,
    IWebSocketSendClient webSocketSendClient,
    IWebSocketFunctionRepository functionRepository,
    HistoryHttpMemoryService historyHttpService,
    ILogger<WebSocketQueuesWorker> logger,
    IMasterService masterService,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.QueuesDelayMilliseconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Dictionary<string, IList<WebSocketRequestToWait>> processingTasks = new();
        Dictionary<string, int> setTickLastCallCounterDictionary = new();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);

                if (!masterService.IsMaster)
                {
                    continue;
                }

                var virtualDeployments = functionRepository.GetVirtualDeployments();
                foreach (var deployment in virtualDeployments)
                {
                    await ProcessQueueAsync(deployment, processingTasks, setTickLastCallCounterDictionary, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error in WebSocketQueuesWorker");
            }
        }
    }

    private async Task ProcessQueueAsync(
        DeploymentInformation deployment,
        Dictionary<string, IList<WebSocketRequestToWait>> processingTasks,
        Dictionary<string, int> setTickLastCallCounterDictionary,
        CancellationToken ct)
    {
        string functionName = deployment.Deployment;

        // Initialiser les collections si nécessaire
        if (!processingTasks.ContainsKey(functionName))
        {
            processingTasks[functionName] = new List<WebSocketRequestToWait>();
        }
        setTickLastCallCounterDictionary.TryAdd(functionName, 0);

        // 1. Gérer les tâches terminées (comme ManageProcessingTasksAsync dans SlimQueuesWorker)
        int numberProcessingTasks = await ManageProcessingTasksAsync(functionName, processingTasks);

        // 2. Calculer le nombre de pods (connexions) ready
        var connections = registry.GetConnections(functionName);
        int numberPodsReady = connections.Count;

        if (numberPodsReady == 0)
        {
            return;
        }

        // 3. Calculer les limites — identique à SlimQueuesWorker
        int numberMaxProcessingTasks = Math.Min(
            deployment.NumberParallelRequest,
            numberPodsReady * deployment.NumberParallelRequestPerPod);
        int numberLimitProcessingTasks = numberMaxProcessingTasks - numberProcessingTasks;

        // 4. Tick last call / queue length
        setTickLastCallCounterDictionary[functionName]++;
        int functionReplicas = deployment.Replicas;

        long queueLength = await UpdateTickLastCallIfRequestStillInProgress(
            functionReplicas,
            setTickLastCallCounterDictionary,
            functionName,
            numberProcessingTasks,
            numberLimitProcessingTasks);

        if (functionReplicas == 0 || queueLength <= 0)
        {
            return;
        }

        if (numberProcessingTasks >= numberMaxProcessingTasks)
        {
            return;
        }

        // 5. Dequeue et lancer les tâches (fire-and-forget, trackées)
        await DispatchRequestsAsync(functionName, processingTasks, numberLimitProcessingTasks, deployment, ct);
    }

    private async Task DispatchRequestsAsync(
        string functionName,
        Dictionary<string, IList<WebSocketRequestToWait>> processingTasks,
        int numberLimitProcessingTasks,
        DeploymentInformation deployment,
        CancellationToken ct)
    {
        var items = await slimFaasQueue.DequeueAsync(functionName, numberLimitProcessingTasks);
        if (items == null || items.Count == 0)
        {
            return;
        }

        foreach (var item in items)
        {
            CustomRequest customRequest;
            try
            {
                customRequest = MemoryPackSerializer.Deserialize<CustomRequest>(item.Data);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to deserialize CustomRequest for WebSocket function {FunctionName}", functionName);
                await slimFaasQueue.ListCallbackAsync(functionName, new ListQueueItemStatus
                {
                    Items = [new QueueItemStatus(item.Id, 500)]
                });
                continue;
            }

            logger.LogDebug("WebSocket dispatch: {Method} {Path} for {FunctionName} elementId={ElementId}",
                customRequest.Method, customRequest.Path, functionName, item.Id);

            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);

            // Lancer la tâche SANS l'attendre (fire-and-forget, trackée)
            Task<int> task = webSocketSendClient.SendAsync(
                functionName,
                customRequest,
                item.Id,
                item.IsLastTry,
                item.TryNumber,
                ct);

            processingTasks[functionName].Add(new WebSocketRequestToWait(task, functionName, item.Id));
        }
    }

    private async Task<int> ManageProcessingTasksAsync(
        string functionName,
        Dictionary<string, IList<WebSocketRequestToWait>> processingTasks)
    {
        var listQueueItemStatus = new ListQueueItemStatus();
        var queueItemStatusList = new List<QueueItemStatus>();
        listQueueItemStatus.Items = queueItemStatusList;

        List<WebSocketRequestToWait> toRemove = new();
        IList<WebSocketRequestToWait> requestToWaits = processingTasks[functionName];

        foreach (var processing in requestToWaits)
        {
            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);

            if (!processing.Task.IsCompleted)
            {
                continue;
            }

            try
            {
                int statusCode = await processing.Task;
                logger.LogDebug(
                    "WebSocket async completed for {FunctionName} elementId={ElementId} statusCode={StatusCode}",
                    functionName, processing.Id, statusCode);
                toRemove.Add(processing);

                if (statusCode == 202)
                {
                    // Le client prendra en charge le callback lui-même
                    logger.LogInformation(
                        "WebSocket async accepted (202) for {FunctionName}/{ElementId}, awaiting client callback",
                        functionName, processing.Id);
                }
                else
                {
                    queueItemStatusList.Add(new QueueItemStatus(processing.Id, statusCode));
                }
            }
            catch (Exception e)
            {
                queueItemStatusList.Add(new QueueItemStatus(processing.Id, 500));
                toRemove.Add(processing);
                logger.LogWarning("WebSocket request error: {Message} {StackTrace}", e.Message, e.StackTrace);
            }
        }

        if (listQueueItemStatus.Items.Count > 0)
        {
            await slimFaasQueue.ListCallbackAsync(functionName, listQueueItemStatus);
        }

        foreach (var item in toRemove)
        {
            requestToWaits.Remove(item);
        }

        return requestToWaits.Count;
    }

    private async Task<long> UpdateTickLastCallIfRequestStillInProgress(
        int functionReplicas,
        Dictionary<string, int> setTickLastCallCounterDictionary,
        string functionName,
        int numberProcessingTasks,
        int numberLimitProcessingTasks)
    {
        int counterLimit = functionReplicas == 0 ? 10 : 40;
        long queueLength = await slimFaasQueue.CountElementAsync(
            functionName,
            [CountType.Available, CountType.Running, CountType.WaitingForRetry]);

        if (setTickLastCallCounterDictionary[functionName] > counterLimit)
        {
            setTickLastCallCounterDictionary[functionName] = 0;
            if (queueLength > 0 || numberProcessingTasks > 0)
            {
                historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
            }
        }

        if (queueLength == 0)
        {
            return 0;
        }

        return await slimFaasQueue.CountElementAsync(
            functionName,
            [CountType.Available],
            numberLimitProcessingTasks);
    }
}

