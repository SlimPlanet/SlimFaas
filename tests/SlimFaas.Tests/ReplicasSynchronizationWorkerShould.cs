﻿using System.Collections;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests;

public class DeploymentsTestData : IEnumerable<object[]>
{
    public IEnumerator<object[]> GetEnumerator()
    {
        yield return new object[]
        {
            new DeploymentsInformations(new List<DeploymentInformation>(),
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()), new List<PodInformation>())
        };
        yield return new object[]
        {
            new DeploymentsInformations(
                new List<DeploymentInformation>
                {
                    new("fibonacci1", "default", Replicas: 1, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration()),
                    new("fibonacci2", "default", Replicas: 0, Pods: new List<PodInformation>(), Configuration: new SlimFaasConfiguration())
                },
                new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
                new List<PodInformation>()
            )
        };
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
/*
public class ReplicasSynchronizationWorkerShould
{
    [Theory]
    [ClassData(typeof(DeploymentsTestData))]
    public async Task SynchroniseDeploymentsShouldCallKubernetesApiWhenMaster(DeploymentsInformations deploymentsInformations)
    {
        Mock<ILogger<ReplicasSynchronizationWorker>> logger = new();
        Mock<IKubernetesService> kubernetesService = new();
        kubernetesService.Setup(k => k.ListFunctionsAsync(It.IsAny<string>())).ReturnsAsync(deploymentsInformations);
        Mock<IMasterService> masterService = new();
        HistoryHttpMemoryService historyHttpService = new();
        Mock<ILogger<ReplicasService>> loggerReplicasService = new();
        ReplicasService replicasService =
            new(kubernetesService.Object, historyHttpService, loggerReplicasService.Object);
        masterService.Setup(ms => ms.IsMaster).Returns(true);
        Mock<IRaftCluster> raftCluster = new();
        raftCluster.Setup(ms => ms.LeadershipToken.IsCancellationRequested).Returns(false);
        raftCluster.Setup(ms => ms.Leader).Returns((new Mock<RaftClusterMember>()).Object);
        Mock<IDatabaseService> databaseServiceMock = new ();
        databaseServiceMock.Setup(db => db.GetAsync(ReplicasSynchronizationWorker.kubernetesDeployments)).ReturnsAsync(String.Empty);
        databaseServiceMock.Setup(db => db.SetAsync(ReplicasSynchronizationWorker.kubernetesDeployments, It.IsAny<string>()));

        ReplicasSynchronizationWorker service = new(replicasService, raftCluster.Object, databaseServiceMock.Object, logger.Object, 100);
        Task task = service.StartAsync(CancellationToken.None);
        await Task.Delay(300);

        Assert.True(task.IsCompleted);
        kubernetesService.Verify(v => v.ListFunctionsAsync(It.IsAny<string>()));
        databaseServiceMock.Verify(v => v.SetAsync(It.IsAny<string>(), It.IsAny<string>()));
    }

    [Fact]
    public async Task LogErrorWhenExceptionIsThrown()
    {
        Mock<ILogger<ReplicasSynchronizationWorker>> logger = new Mock<ILogger<ReplicasSynchronizationWorker>>();
        Mock<IKubernetesService> kubernetesService = new Mock<IKubernetesService>();
        kubernetesService.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>())).Throws(new Exception());
        Mock<IRaftCluster> raftCluster = new();
        raftCluster.Setup(ms => ms.LeadershipToken).Returns(() => new CancellationToken());
        raftCluster.Setup(ms => ms.Leader).Returns((new Mock<RaftClusterMember>()).Object);
        HistoryHttpMemoryService historyHttpService = new HistoryHttpMemoryService();
        Mock<ILogger<ReplicasService>> loggerReplicasService = new Mock<ILogger<ReplicasService>>();

        var autoScaler = new AutoScaler(null, null, null);
        var metricsRegistry = new Mock<IRequestedMetricsRegistry>().Object;
        var slimFaasOptions = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
        {
            PodScaledUpByDefaultWhenInfrastructureHasNeverCalled = false
        });

        ReplicasService replicasService =
            new ReplicasService(kubernetesService.Object,
                historyHttpService,
                autoScaler,
                loggerReplicasService.Object,
                metricsRegistry,
                slimFaasOptions);


        Mock<IDatabaseService> databaseService = new Mock<IDatabaseService>();
        ReplicasSynchronizationWorker service = new ReplicasSynchronizationWorker(replicasService, raftCluster.Object, databaseService.Object, logger.Object, 10);
        Task task = service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.AtLeastOnce);

        Assert.True(task.IsCompleted);
    }
}*/
