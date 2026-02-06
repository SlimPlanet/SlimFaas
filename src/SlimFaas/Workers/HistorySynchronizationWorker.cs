﻿using Microsoft.Extensions.Options;
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
                    long ticksMemory = historyHttpMemoryService.GetTicksLastCall(function.Deployment);
                    bool isDatabaseTicksUpdated = false;
                    var nowTicks = DateTime.UtcNow.Ticks;
                    if (ticksInDatabase > nowTicks)
                    {
                        logger.LogWarning(
                            "HistorySynchronizationWorker: ticksInDatabase is superior to now ticks {TimeSpan} for {Function}",
                            TimeSpan.FromTicks(ticksInDatabase - nowTicks), function.Deployment);
                        ticksInDatabase = nowTicks;
                        isDatabaseTicksUpdated = true;
                    }
                    if (ticksMemory > nowTicks)
                    {
                        logger.LogWarning(
                            "HistorySynchronizationWorker: ticksMemory is superior to now ticks {TimeSpan} for {Function}",
                            TimeSpan.FromTicks(ticksMemory - nowTicks), function.Deployment);
                        ticksMemory = nowTicks;
                    }

                    if (ticksInDatabase > ticksMemory || isDatabaseTicksUpdated)
                    {
                        logger.LogDebug("HistorySynchronizationWorker: Synchronizing history for {Function} to {Ticks} from Database", function.Deployment, ticksInDatabase);
                        historyHttpMemoryService.SetTickLastCall(function.Deployment, ticksInDatabase);
                    }
                    else if (ticksInDatabase < ticksMemory)
                    {
                        logger.LogDebug("HistorySynchronizationWorker: Synchronizing history for {Function} to {Ticks} from Memory", function.Deployment, ticksMemory);
                        await historyHttpDatabaseService.SetTickLastCallAsync(function.Deployment, ticksMemory);
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in HistorySynchronizationWorker");
            }
        }
    }
}
