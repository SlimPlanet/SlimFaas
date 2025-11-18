namespace SlimFaas;

public class ScaleReplicasWorker(IReplicasService replicasService, IMasterService masterService,
        ILogger<ScaleReplicasWorker> logger,
        int delay = EnvironmentVariables.ScaleReplicasWorkerDelayMillisecondsDefault)
    : BackgroundService
{
    private readonly int _delay =
        EnvironmentVariables.ReadInteger(logger, EnvironmentVariables.ScaleReplicasWorkerDelayMilliseconds, delay);

    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

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
