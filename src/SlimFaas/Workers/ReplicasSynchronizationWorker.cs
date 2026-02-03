﻿using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public class ReplicasSynchronizationWorker(
    IReplicasService replicasService,
    ILogger<ReplicasSynchronizationWorker> logger,
    IOptions<SlimFaasOptions> slimFaasOptions,
    IOptions<WorkersOptions> workersOptions,
    INamespaceProvider namespaceProvider)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.ReplicasSynchronizationDelayMilliseconds;
    private readonly string _namespace = namespaceProvider.CurrentNamespace;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);

                await replicasService.SyncDeploymentsAsync(_namespace);

            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
