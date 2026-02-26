using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Jobs;


public class SlimJobsConfigurationWorker(IJobConfiguration jobConfiguration,
    ILogger<SlimJobsConfigurationWorker> logger,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.JobsConfigurationDelayMilliseconds;

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
            await Task.Delay(_delay, stoppingToken);

            await jobConfiguration.SyncJobsConfigurationAsync();

        }
        catch (Exception e)
        {
            logger.LogError(e, "Global error in slimFaas jobs configuration worker");
        }
    }
}
