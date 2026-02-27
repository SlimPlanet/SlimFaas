using MemoryPack;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using Microsoft.Extensions.Options;

namespace SlimFaas.WebSocket;

/// <summary>
/// Worker qui poll les queues des fonctions WebSocket virtuelles
/// et dispatch les messages via WebSocket au lieu d'HTTP.
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
                    await ProcessQueueAsync(deployment, stoppingToken);
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
        CancellationToken ct)
    {
        string functionName = deployment.Deployment;
        var connections = registry.GetConnections(functionName);

        if (connections.Count == 0)
        {
            return;
        }

        // Nombre de slots disponibles (parallélisme limité par la config)
        int maxParallel = Math.Min(
            deployment.NumberParallelRequest,
            connections.Count * deployment.NumberParallelRequestPerPod);

        int active = connections.Sum(c => c.ActiveRequests);
        int available = maxParallel - active;

        if (available <= 0)
        {
            return;
        }

        long queueCount = await slimFaasQueue.CountElementAsync(
            functionName,
            [CountType.Available],
            available);

        if (queueCount <= 0)
        {
            return;
        }

        historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);

        var items = await slimFaasQueue.DequeueAsync(functionName, available);
        if (items == null || items.Count == 0)
        {
            return;
        }

        var tasks = items.Select(item => DispatchItemAsync(functionName, item, ct)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task DispatchItemAsync(
        string functionName,
        QueueData item,
        CancellationToken ct)
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
            return;
        }

        logger.LogDebug("WebSocket dispatch: {Method} {Path} for {FunctionName} elementId={ElementId}",
            customRequest.Method, customRequest.Path, functionName, item.Id);

        historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);

        int statusCode = await webSocketSendClient.SendAsync(
            functionName,
            customRequest,
            item.Id,
            item.IsLastTry,
            item.TryNumber,
            ct);

        if (statusCode == 202)
        {
            // Le client prendra en charge le callback lui-même
            logger.LogInformation("WebSocket async accepted (202) for {FunctionName}/{ElementId}, awaiting client callback",
                functionName, item.Id);
        }
        else
        {
            await slimFaasQueue.ListCallbackAsync(functionName, new ListQueueItemStatus
            {
                Items = [new QueueItemStatus(item.Id, statusCode)]
            });
        }
    }
}

