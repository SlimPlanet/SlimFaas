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
    // Avec les watch actifs, les événements CronJob pilotent la synchronisation et le
    // délai devient un resync de sécurité ; sans watch, l'attente est équivalente au
    // Task.Delay historique (aucun pulse n'arrive jamais).
    private readonly TimeSpan _maximumWait = watchSignals.WatchEnabled
        ? TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.JobsConfigurationResyncSeconds)
        : TimeSpan.FromMilliseconds(workersOptions.Value.JobsConfigurationDelayMilliseconds);

    private long _observedVersion = watchSignals.JobsConfiguration.Version;

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
            _observedVersion = await watchSignals.JobsConfiguration.WaitForChangeAsync(
                _observedVersion,
                _maximumWait,
                stoppingToken);

            await jobConfiguration.SyncJobsConfigurationAsync();

        }
        catch (Exception e)
        {
            logger.LogError(e, "Global error in slimFaas jobs configuration worker");
        }
    }
}
