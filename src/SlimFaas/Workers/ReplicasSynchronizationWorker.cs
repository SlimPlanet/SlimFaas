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
    // les événements et le délai devient un simple resync de sécurité. Sans watch
    // (orchestrateur non Kubernetes ou option désactivée) ou tant qu'un flux est
    // indisponible (RBAC sans verbe "watch", API server injoignable), aucun événement
    // n'est garanti : l'attente redevient strictement équivalente au Task.Delay
    // historique.
    private readonly TimeSpan _legacyDelay =
        TimeSpan.FromMilliseconds(workersOptions.Value.ReplicasSynchronizationDelayMilliseconds);

    private readonly TimeSpan _resyncInterval =
        TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.FunctionsResyncSeconds);

    // Version observée capturée à la construction, donc avant le démarrage de tout
    // hosted service : le watcher ne peut pas encore avoir pulsé.
    private long _observedVersion = watchSignals.Functions.Version;

    private TimeSpan MaximumWait =>
        watchSignals.WatchEnabled && watchSignals.Functions.IsHealthy ? _resyncInterval : _legacyDelay;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                _observedVersion = await watchSignals.Functions.WaitForChangeAsync(
                    _observedVersion,
                    MaximumWait,
                    stoppingToken);

                await replicasService.SyncDeploymentsAsync(_namespace);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
