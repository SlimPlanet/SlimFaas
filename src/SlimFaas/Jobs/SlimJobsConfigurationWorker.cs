using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas.Jobs;


public class SlimJobsConfigurationWorker(IJobConfiguration jobConfiguration,
    ILogger<SlimJobsConfigurationWorker> logger,
    IOptions<WorkersOptions> workersOptions,
    IOptions<SlimFaasOptions> slimFaasOptions,
    KubernetesWatchSignals watchSignals)
    : BackgroundService
{
    // Avec les watch actifs et le flux CronJob connecté, les événements pilotent la
    // synchronisation et le délai devient un resync de sécurité ; sinon l'attente est
    // équivalente au Task.Delay historique (voir KubernetesWatchSyncCadence).
    private readonly KubernetesWatchSyncCadence _cadence = new(
        watchSignals,
        watchSignals.JobsConfiguration,
        TimeSpan.FromMilliseconds(workersOptions.Value.JobsConfigurationDelayMilliseconds),
        TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.JobsConfigurationResyncSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            await DoOneCycle(stoppingToken);
        }
    }

    private async Task DoOneCycle(CancellationToken stoppingToken)
    {
        try
        {
            await _cadence.WaitForSyncDueAsync(stoppingToken);

            // Un LIST CronJob en échec ne lève pas (configuration null conservée) :
            // ne pas valider l'événement consommé, retenter à la cadence historique.
            if (await jobConfiguration.SyncJobsConfigurationAsync())
            {
                _cadence.CommitSync();
            }
            else
            {
                _cadence.MarkSyncFailed();
            }
        }
        catch (Exception e)
        {
            _cadence.MarkSyncFailed();
            logger.LogError(e, "Global error in slimFaas jobs configuration worker");
        }
    }
}
