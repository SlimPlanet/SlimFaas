using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Workers;

public class MetricsWorker(
    IReplicasService replicasService,
    ISlimFaasQueue slimFaasQueue,
    DynamicGaugeService dynamicGaugeService,
    IRaftCluster raftCluster,
    ILogger<MetricsWorker> logger,
    IOptions<WorkersOptions> workersOptions)
    : BackgroundService
{
    private static readonly string[] QueueMetricNames =
    [
        "slimfaas_function_queue_ready_items",
        "slimfaas_function_queue_in_flight_items",
        "slimfaas_function_queue_retry_pending_items"
    ];

    private readonly int _delay = workersOptions.Value.ScaleReplicasDelayMilliseconds;
    private readonly HashSet<string> _publishedFunctions = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken);

                if (raftCluster.Leader is null)
                {
                    continue;
                }

                var deployments = replicasService.Deployments;
                RemoveStaleFunctionMetrics(deployments.Functions);
                foreach (var deployment in deployments.Functions)
                {
                    // Label Prometheus : function="fibonacci1"
                    var labels = new Dictionary<string, string>
                    {
                        ["function"] = deployment.Deployment
                    };

                    // 1) Messages prêts (ready_items)
                    var readyCount = await slimFaasQueue.CountElementAsync(
                        deployment.Deployment,
                        new List<CountType> { CountType.Available });

                    dynamicGaugeService.SetGaugeValue(
                        "slimfaas_function_queue_ready_items",
                        readyCount,
                        "Number of messages currently ready to be processed in the function queue",
                        labels);

                    // 2) Messages en cours (in_flight_items)
                    var inFlightCount = await slimFaasQueue.CountElementAsync(
                        deployment.Deployment,
                        new List<CountType> { CountType.Running });

                    dynamicGaugeService.SetGaugeValue(
                        "slimfaas_function_queue_in_flight_items",
                        inFlightCount,
                        "Number of messages currently being processed by workers for the function",
                        labels);

                    // 3) Messages en attente de retry (retry_pending_items)
                    var retryPendingCount = await slimFaasQueue.CountElementAsync(
                        deployment.Deployment,
                        new List<CountType> { CountType.WaitingForRetry });

                    dynamicGaugeService.SetGaugeValue(
                        "slimfaas_function_queue_retry_pending_items",
                        retryPendingCount,
                        "Number of messages waiting for a retry in the function queue",
                        labels);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global Error in MetricsWorker");
            }
        }
    }

    internal void RemoveStaleFunctionMetrics(IList<DeploymentInformation> functions)
    {
        var currentFunctions = functions
            .Select(static function => function.Deployment)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var staleFunction in _publishedFunctions.Except(currentFunctions).ToArray())
        {
            var labels = new Dictionary<string, string>
            {
                ["function"] = staleFunction
            };
            foreach (var metricName in QueueMetricNames)
            {
                dynamicGaugeService.RemoveGaugeValue(metricName, labels);
            }
        }

        _publishedFunctions.Clear();
        _publishedFunctions.UnionWith(currentFunctions);
    }
}
