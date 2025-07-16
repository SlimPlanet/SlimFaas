using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public class MetricsWorkerShould
{
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
        ReplicasService replicasService =
            new(kubernetesService.Object,
                historyHttpService,
                loggerReplicasService.Object);
        masterService.Setup(ms => ms.IsMaster).Returns(true);
        kubernetesService.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>())).ReturnsAsync(deploymentsInformations);

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
        MetricsWorker service = new(replicasService, slimFaasQueue, dynamicGaugeService, raftCluster.Object, logger.Object, 100);
        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);
        await Task.Delay(3000);


        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

}
