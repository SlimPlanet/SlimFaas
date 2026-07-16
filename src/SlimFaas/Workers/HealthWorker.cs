﻿using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Options;

namespace SlimFaas;

public class HealthWorker(
    IRaftCluster raftCluster,
    ILogger<HealthWorker> logger,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.HealthDelayMilliseconds;
    private readonly int _delayToStartHealthCheck = workersOptions.Value.HealthDelayToStartHealthCheckSeconds;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(1000 * _delayToStartHealthCheck, stoppingToken);
        var consensusWasUnavailable = false;
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);
                var consensusUnavailable = raftCluster.Leader is null || raftCluster.ConsensusToken.IsCancellationRequested;
                if (consensusUnavailable && !consensusWasUnavailable)
                {
                    logger.LogWarning("Raft cluster has no active consensus; the pod remains alive but is not ready");
                }
                else if (!consensusUnavailable && consensusWasUnavailable)
                {
                    logger.LogInformation("Raft cluster consensus is available again");
                }

                consensusWasUnavailable = consensusUnavailable;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in HealthWorker");
            }
        }
    }
}
