using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimFaas;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.MetricsQuery;
using SlimFaas.Scaling;

namespace SlimFaas.Tests;

public class MetricsWorkerShould
{
    private static AutoScaler CreateAutoScalerForTests()
    {
        PromQlMiniEvaluator.SnapshotProvider snapshotProvider = () =>
        {
            var metrics = new Dictionary<string, double> { { "dummy_metric", 1.0 } };
            var pod = new Dictionary<string, IReadOnlyDictionary<string, double>>
            {
                { "pod-0", metrics }
            };
            var deployment = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
            {
                { "dummy-deploy", pod }
            };
            var root = new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            {
                { 1L, deployment }
            };
            return root;
        };

        var evaluator = new PromQlMiniEvaluator(snapshotProvider);
        var store = new InMemoryAutoScalerStore();
        return new AutoScaler(evaluator, store, logger: null);
    }

    [Fact]
    public async Task AddQueueMetrics()
    {
        var deploymentsInformations = new DeploymentsInformations(
            new List<DeploymentInformation>
            {
                new("fibonacci1", "default", Replicas: 1, Pods: new List<PodInformation>(),
                    Configuration: new SlimFaasConfiguration()),
                new("fibonacci2", "default", Replicas: 0, Pods: new List<PodInformation>(),
                    Configuration: new SlimFaasConfiguration())
            },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
            new List<PodInformation>()
        );

        Mock<ILogger<MetricsWorker>> logger = new();
        Mock<IKubernetesService> kubernetesService = new();
        Mock<IMasterService> masterService = new();
        HistoryHttpMemoryService historyHttpService = new();
        Mock<ILogger<ReplicasService>> loggerReplicasService = new();

        var autoScaler = CreateAutoScalerForTests();

        // Nouveau : registry pour coller à la nouvelle signature de ReplicasService
        var metricsRegistry = new Mock<IRequestedMetricsRegistry>().Object;

        ReplicasService replicasService =
            new(kubernetesService.Object,
                historyHttpService,
                autoScaler,
                loggerReplicasService.Object,
                metricsRegistry);

        masterService.Setup(ms => ms.IsMaster).Returns(true);
        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(deploymentsInformations);

        Mock<IRaftClusterMember> raftClusterMember = new();

        Mock<IRaftCluster> raftCluster = new();
        raftCluster.Setup(rc => rc.Leader).Returns(raftClusterMember.Object);

        await replicasService.SyncDeploymentsAsync("default");

        SlimFaasQueue slimFaasQueue = new(new DatabaseMockService());
        CustomRequest customRequest =
            new(new List<CustomHeader> { new() { Key = "key", Values = new[] { "value1" } } },
                new byte[1], "fibonacci1", "/download", "GET", "");
        var jsonCustomRequest = MemoryPackSerializer.Serialize(customRequest);
        var retryInformation = new RetryInformation([], 30, []);
        await slimFaasQueue.EnqueueAsync("fibonacci1", jsonCustomRequest, retryInformation);

        var dynamicGaugeService = new DynamicGaugeService();
        MetricsWorker service =
            new(replicasService, slimFaasQueue, dynamicGaugeService, raftCluster.Object, logger.Object, 100);

        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);

        await Task.Delay(3000);

        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }
}
