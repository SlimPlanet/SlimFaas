using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public class HistorySynchronizationWorker(
    IReplicasService replicasService,
    HistoryHttpMemoryService historyHttpMemoryService,
    HistoryHttpDatabaseService historyHttpDatabaseService,
    ILogger<HistorySynchronizationWorker> logger,
    ISlimDataStatus slimDataStatus,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.HistorySynchronizationDelayMilliseconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await slimDataStatus.WaitForReadyAsync();
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);

                foreach (DeploymentInformation function in replicasService.Deployments.Functions)
                {
                    long ticksInDatabase = await historyHttpDatabaseService.GetTicksLastCallAsync(function.Deployment);
                    bool isDatabaseTicksUpdated = false;
                    var nowTicks = DateTime.UtcNow.Ticks;
                    historyHttpMemoryService.RefreshActiveCall(function.Deployment, nowTicks);
                    long ticksMemory = historyHttpMemoryService.GetTicksLastCall(function.Deployment);
                    if (ticksInDatabase > nowTicks)
                    {
                        logger.LogHistorySynchronizationWorkerTicksInDatabaseIsSuperiorToNow(TimeSpan.FromTicks(ticksInDatabase - nowTicks), function.Deployment);
                        ticksInDatabase = nowTicks;
                        isDatabaseTicksUpdated = true;
                    }
                    if (ticksMemory > nowTicks)
                    {
                        logger.LogHistorySynchronizationWorkerTicksMemoryIsSuperiorToNow(TimeSpan.FromTicks(ticksMemory - nowTicks), function.Deployment);
                        ticksMemory = nowTicks;
                    }

                    if (ticksInDatabase > ticksMemory || isDatabaseTicksUpdated)
                    {
                        logger.LogHistorySynchronizationWorkerSynchronizingHistoryForToFrom(function.Deployment, ticksInDatabase);
                        historyHttpMemoryService.SetTickLastCall(function.Deployment, ticksInDatabase);
                    }
                    else if (ticksInDatabase < ticksMemory)
                    {
                        logger.LogHistorySynchronizationWorkerSynchronizingHistoryForToFrom2(function.Deployment, ticksMemory);
                        await historyHttpDatabaseService.SetTickLastCallAsync(function.Deployment, ticksMemory);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogGlobalErrorInHistorySynchronizationWorker(e);
            }
        }
    }
}
