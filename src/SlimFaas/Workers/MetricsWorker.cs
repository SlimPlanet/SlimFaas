using DotNext.Net.Cluster.Consensus.Raft;
using SlimFaas.Database;

namespace SlimFaas;

public class MetricsWorker(IReplicasService replicasService, ISlimFaasQueue slimFaasQueue, DynamicGaugeService dynamicGaugeService, IRaftCluster raftCluster,
        ILogger<MetricsWorker> logger,
        int delay = EnvironmentVariables.ScaleReplicasWorkerDelayMillisecondsDefault)
    : BackgroundService
{
    private readonly int _delay =
        EnvironmentVariables.ReadInteger(logger, EnvironmentVariables.ScaleReplicasWorkerDelayMilliseconds, delay);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (stoppingToken.IsCancellationRequested == false)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);
                if (raftCluster.Leader == null)
                {
                    continue;
                }
                var deployments = replicasService.Deployments;
                foreach (var deployment in deployments.Functions)
                {
                    var deploymentName = deployment.Deployment.Replace("-", "_").ToLowerInvariant();
                    var numberElementAvailable = await slimFaasQueue.CountElementAsync(deployment.Deployment, new List<CountType>() { CountType.Available });
                    dynamicGaugeService.SetGaugeValue(
                        $"slimfaas_queue_available_{deploymentName}_length",
                        numberElementAvailable, "Current number of elements available in the queue");

                    var numberElementProcessing = await slimFaasQueue.CountElementAsync(deployment.Deployment, new List<CountType>() { CountType.Running });
                    dynamicGaugeService.SetGaugeValue(
                        $"slimfaas_queue_processing_{deploymentName}_length",
                        numberElementProcessing, "Current number of elements processing in the queue");

                    var numberElementWaitingForRetry = await slimFaasQueue.CountElementAsync(deployment.Deployment, new List<CountType>() { CountType.WaitingForRetry });
                    dynamicGaugeService.SetGaugeValue(
                        $"slimfaas_queue_waiting_for_retry_{deploymentName}_length",
                        numberElementWaitingForRetry, "Current number of elements waiting for retry in the queue");
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in MetricsWorker");
            }
        }
    }
}
