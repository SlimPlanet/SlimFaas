using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public class HistorySynchronizationWorkerShould
{
    [Fact]
    public async Task SyncLastTicksBetweenDatabaseAndMemory()
    {
        Mock<ILogger<HistorySynchronizationWorker>> logger = new Mock<ILogger<HistorySynchronizationWorker>>();
        DatabaseMockService redisMockService = new DatabaseMockService();
        HistoryHttpDatabaseService historyHttpRedisService = new HistoryHttpDatabaseService(redisMockService);
        Mock<IKubernetesService> kubernetesService = new Mock<IKubernetesService>();
        DeploymentsInformations deploymentsInformations = new DeploymentsInformations(
            new List<DeploymentInformation>
            {
                new("fibonacci1", "default", Replicas: 1, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration()),
                new("fibonacci2", "default", Replicas: 0, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration())
            },
            new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>());
        kubernetesService.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>())).ReturnsAsync(deploymentsInformations);
        HistoryHttpMemoryService historyHttpMemoryService = new HistoryHttpMemoryService();
        Mock<ILogger<ReplicasService>> loggerReplicasService = new Mock<ILogger<ReplicasService>>();

        ReplicasService replicasService = new ReplicasService(kubernetesService.Object,
            historyHttpMemoryService,
            loggerReplicasService.Object);

        Mock<ISlimDataStatus> slimDataStatus = new Mock<ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        await replicasService.SyncDeploymentsAsync("default");

        long firstTicks = 1L;
        await historyHttpRedisService.SetTickLastCallAsync("fibonacci1", firstTicks);
        HistorySynchronizationWorker service = new(replicasService,
            historyHttpMemoryService, historyHttpRedisService, logger.Object, slimDataStatus.Object, 100);

        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);
        await Task.Delay(500);
        long ticksFirstCallAsync = historyHttpMemoryService.GetTicksLastCall("fibonacci1");
        Assert.Equal(firstTicks, ticksFirstCallAsync);

        long secondTicks = 2L;
        historyHttpMemoryService.SetTickLastCall("fibonacci1", secondTicks);
        await Task.Delay(200);
        long ticksSecondCallAsync = await historyHttpRedisService.GetTicksLastCallAsync("fibonacci1");
        Assert.Equal(secondTicks, ticksSecondCallAsync);

        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task LogErrorWhenExceptionIsThrown()
    {
        Mock<ILogger<HistorySynchronizationWorker>> logger = new Mock<ILogger<HistorySynchronizationWorker>>();
        DatabaseMockService redisMockService = new DatabaseMockService();
        HistoryHttpDatabaseService historyHttpRedisService = new HistoryHttpDatabaseService(redisMockService);
        HistoryHttpMemoryService historyHttpMemoryService = new HistoryHttpMemoryService();
        Mock<IReplicasService> replicasService = new Mock<IReplicasService>();
        replicasService.Setup(r => r.Deployments).Throws(new Exception());
        Mock<ISlimDataStatus> slimDataStatus = new Mock<ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        HistorySynchronizationWorker service = new HistorySynchronizationWorker(replicasService.Object,
            historyHttpMemoryService, historyHttpRedisService, logger.Object, slimDataStatus.Object, 10);

        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);
        await Task.Delay(100);

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.AtLeastOnce);
        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }
}
