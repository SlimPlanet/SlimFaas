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

    // Avec les watch actifs, la synchronisation est pilotée par les événements et le
    // délai devient un simple resync de sécurité ; sans watch (orchestrateur non
    // Kubernetes ou option désactivée), aucun pulse n'arrive jamais et l'attente est
    // strictement équivalente au Task.Delay historique.
    private readonly TimeSpan _maximumWait = watchSignals.WatchEnabled
        ? TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.FunctionsResyncSeconds)
        : TimeSpan.FromMilliseconds(workersOptions.Value.ReplicasSynchronizationDelayMilliseconds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long observedVersion = watchSignals.Functions.Version;
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                observedVersion = await watchSignals.Functions.WaitForChangeAsync(
                    observedVersion,
                    _maximumWait,
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
