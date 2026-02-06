﻿using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Options;

namespace SlimFaas;

public class HealthWorker(
    IHostApplicationLifetime hostApplicationLifetime,
    IRaftCluster raftCluster,
    ILogger<HealthWorker> logger,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.HealthDelayMilliseconds;
    private readonly int _delayToExitSeconds = workersOptions.Value.HealthDelayToExitSeconds;
    private readonly int _delayToStartHealthCheck = workersOptions.Value.HealthDelayToStartHealthCheckSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000 * _delayToStartHealthCheck, stoppingToken);
        TimeSpan timeSpan = TimeSpan.FromSeconds(0);
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);
                if (raftCluster.Leader == null)
                {
                    timeSpan = timeSpan.Add(TimeSpan.FromMilliseconds(_delay));
                    logger.LogWarning("Raft cluster has no leader");
                }
                else
                {
                    timeSpan = TimeSpan.FromSeconds(0);
                }

                if (timeSpan.TotalSeconds > _delayToExitSeconds)
                {
                    logger.LogError("Raft cluster has no leader for more than {TotalSeconds} seconds, exist the application ", timeSpan.TotalSeconds);
                    hostApplicationLifetime.StopApplication();
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in HealthWorker");
            }
        }
    }
}
