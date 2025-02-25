using SlimFaas.Kubernetes;

namespace SlimFaas;

public class ReplicasSynchronizationWorker(IReplicasService replicasService,
    IJobService jobService,
        ILogger<ReplicasSynchronizationWorker> logger,
        int delay = EnvironmentVariables.ReplicasSynchronizationWorkerDelayMillisecondsDefault)
    : BackgroundService
{
    private readonly int _delay = EnvironmentVariables.ReadInteger(logger,
        EnvironmentVariables.ReplicasSynchronisationWorkerDelayMilliseconds, delay);

    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);

                await replicasService.SyncDeploymentsAsync(_namespace);
                var jobs = await jobService.SyncJobsAsync();
                foreach (Job job in jobs)
                {
                   if(job.Status != JobStatus.Running)
                   {
                       await jobService.DeleteJobAsync(job.Name);
                   }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in ScaleReplicasWorker");
            }
        }
    }
}
