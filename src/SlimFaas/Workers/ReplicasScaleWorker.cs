﻿﻿using Microsoft.Extensions.Options;
  using SlimFaas.Kubernetes;
  using SlimFaas.Options;
using SlimFaas.Scaling;
using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimFaas;

public class ScaleReplicasWorker(
    IReplicasService replicasService,
    IMasterService masterService,
    ILogger<ScaleReplicasWorker> logger,
    IOptions<WorkersOptions> workersOptions,
    INamespaceProvider namespaceProvider,
    ScalingDiagnosticsStore? diagnostics = null,
    IRaftCluster? cluster = null)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.ScaleReplicasDelayMilliseconds;
    private readonly string _namespace = namespaceProvider.CurrentNamespace;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);
                bool isMaster = masterService.IsMaster;
                diagnostics?.SetLeadership(isMaster);
                if (!isMaster)
                {
                    continue;
                }

                // Cancellation ends the diagnostic session immediately, even during an in-flight replica write.
                using var leadership = (cluster?.LeadershipToken ?? CancellationToken.None)
                    .Register(() => diagnostics?.SetLeadership(false));
                await replicasService.CheckScaleAsync(_namespace);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
