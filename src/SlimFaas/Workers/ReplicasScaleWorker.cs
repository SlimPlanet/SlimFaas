﻿﻿using Microsoft.Extensions.Options;
  using SlimFaas.Kubernetes;
  using SlimFaas.Options;

namespace SlimFaas;

public class ScaleReplicasWorker(
    IReplicasService replicasService,
    IMasterService masterService,
    ILogger<ScaleReplicasWorker> logger,
    IOptions<SlimFaasOptions> slimFaasOptions,
    IOptions<WorkersOptions> workersOptions,
    INamespaceProvider namespaceProvider)
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
                if (!masterService.IsMaster)
                {
                    continue;
                }

                await replicasService.CheckScaleAsync(_namespace);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
