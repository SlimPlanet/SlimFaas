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
    // synchronisation et le délai devient un resync de sécurité. Sans watch, ou tant
    // que le flux est indisponible (RBAC sans verbe "watch", API server injoignable),
    // l'attente est équivalente au Task.Delay historique.
    private readonly TimeSpan _legacyDelay =
        TimeSpan.FromMilliseconds(workersOptions.Value.JobsConfigurationDelayMilliseconds);

    private readonly TimeSpan _resyncInterval =
        TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.JobsConfigurationResyncSeconds);

    // Version observée capturée à la construction, donc avant le démarrage de tout
    // hosted service : le watcher ne peut pas encore avoir pulsé.
    private long _observedVersion = watchSignals.JobsConfiguration.Version;

    private TimeSpan MaximumWait =>
        watchSignals.WatchEnabled && watchSignals.JobsConfiguration.IsHealthy ? _resyncInterval : _legacyDelay;

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
                MaximumWait,
                stoppingToken);

            await jobConfiguration.SyncJobsConfigurationAsync();

        }
        catch (Exception e)
        {
            logger.LogError(e, "Global error in slimFaas jobs configuration worker");
        }
    }
}
