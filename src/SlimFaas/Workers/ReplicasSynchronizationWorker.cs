using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas;

public class ReplicasSynchronizationWorker(
    IReplicasService replicasService,
    ILogger<ReplicasSynchronizationWorker> logger,
    IOptions<WorkersOptions> workersOptions,
    IOptions<SlimFaasOptions> slimFaasOptions,
    KubernetesWatchSignals watchSignals,
    INamespaceProvider namespaceProvider)
    : BackgroundService
{
    private readonly string _namespace = namespaceProvider.CurrentNamespace;

    // Avec les watch actifs et les flux connectés, la synchronisation est pilotée par
    // les événements et le délai devient un simple resync de sécurité ; sinon
    // l'attente est équivalente au Task.Delay historique (voir KubernetesWatchSyncCadence).
    private readonly KubernetesWatchSyncCadence _cadence = new(
        watchSignals,
        watchSignals.Functions,
        TimeSpan.FromMilliseconds(workersOptions.Value.ReplicasSynchronizationDelayMilliseconds),
        TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.FunctionsResyncSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await _cadence.WaitForSyncDueAsync(stoppingToken);

                await replicasService.SyncDeploymentsAsync(_namespace);
                _cadence.CommitSync();
            }
            catch (Exception e)
            {
                _cadence.MarkSyncFailed();
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
