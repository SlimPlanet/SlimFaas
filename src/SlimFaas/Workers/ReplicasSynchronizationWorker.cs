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

                DeploymentsInformations before = replicasService.Deployments;
                DeploymentsInformations after = await replicasService.SyncDeploymentsAsync(_namespace);
                // Contrat avec ListFunctionsAsync (Kubernetes et Process) : un LIST en
                // échec ne lève pas, il renvoie le snapshot précédent INCHANGÉ pour la
                // résilience des appels de démarrage. La même instance signifie donc
                // « pas de donnée fraîche » : l'événement consommé ne doit pas être
                // validé, le retry suit la cadence historique au lieu du resync.
                if (ReferenceEquals(before, after))
                {
                    _cadence.MarkSyncFailed();
                }
                else
                {
                    _cadence.CommitSync();
                }
            }
            catch (Exception e)
            {
                _cadence.MarkSyncFailed();
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
