using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Kubernetes;
using MemoryPack;
using SlimFaas.Database;
using SlimData;
using SlimFaas.Options;
namespace SlimFaas.Tests;

public class SlimWorkerShould
{
    [Fact]
    public async Task OnlyCallOneFunctionAsync()
    {
        HttpResponseMessage responseMessage = new HttpResponseMessage();
        responseMessage.StatusCode = HttpStatusCode.OK;

        Mock<ISendClient> sendClientMock = new Mock<ISendClient>();
        sendClientMock.Setup(s => s.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<Proxy?>()))
            .ReturnsAsync(responseMessage);

        Mock<IServiceProvider> serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(x => x.GetService(typeof(ISendClient)))
            .Returns(sendClientMock.Object);

        Mock<IServiceScope> serviceScope = new Mock<IServiceScope>();
        serviceScope.Setup(x => x.ServiceProvider).Returns(serviceProvider.Object);

        Mock<IServiceScopeFactory> serviceScopeFactory = new Mock<IServiceScopeFactory>();
        serviceScopeFactory
            .Setup(x => x.CreateScope())
            .Returns(serviceScope.Object);

        serviceProvider
            .Setup(x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(serviceScopeFactory.Object);

        Mock<ISlimDataStatus> slimDataStatus = new Mock<ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        Mock<IMasterService> masterService = new Mock<IMasterService>();
        masterService.Setup(s => s.IsMaster).Returns(true);

        Mock<IReplicasService> replicasService = new Mock<IReplicasService>();
        replicasService.Setup(rs => rs.Deployments).Returns(new DeploymentsInformations(
            SlimFaas: new SlimFaasDeploymentInformation(2, new List<PodInformation>()),
            Functions: new List<DeploymentInformation>
            {
                new(Replicas: 1, Deployment: "fibonacci", Namespace: "default", NumberParallelRequest: 1,
                    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 300,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true, Configuration: new SlimFaasConfiguration(),
                    Pods: new List<PodInformation> { new("", true, true, "1", "")}, EndpointReady: true),
                new(Replicas: 1, Deployment: "no-pod-started", Namespace: "default", NumberParallelRequest: 1,
                    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 300,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true, Configuration: new SlimFaasConfiguration(),
                    Pods: new List<PodInformation> { new("", false, false, "", "")}, EndpointReady: true),
                new(Replicas: 0, Deployment: "no-replicas", Namespace: "default", NumberParallelRequest: 1,
                    ReplicasMin: 0, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 300, Configuration: new SlimFaasConfiguration(),
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true, Pods: new List<PodInformation>(), EndpointReady: false)
            }, Pods: new List<PodInformation>()));
        HistoryHttpMemoryService historyHttpService = new HistoryHttpMemoryService();
        Mock<ILogger<SlimQueuesWorker>> logger = new Mock<ILogger<SlimQueuesWorker>>();

        SlimFaasQueue slimFaasQueue = new SlimFaasQueue(new DatabaseMockService());
        CustomRequest customRequest =
            new CustomRequest(new List<CustomHeader> { new() { Key = "key", Values = new[] { "value1" } } },
                new byte[1], "fibonacci", "/download", "GET", "");
        var jsonCustomRequest = MemoryPackSerializer.Serialize(customRequest);
        var retryInformation = new RetryInformation([], 30, []);
        await slimFaasQueue.EnqueueAsync("fibonacci", jsonCustomRequest, retryInformation);

        CustomRequest customRequestNoPodStarted =
            new CustomRequest(new List<CustomHeader> { new() { Key = "key", Values = new[] { "value1" } } },
                new byte[1], "no-pod-started", "/download", "GET", "");
        var jsonCustomNoPodStarted = MemoryPackSerializer.Serialize(customRequestNoPodStarted);
        await slimFaasQueue.EnqueueAsync("no-pod-started", jsonCustomNoPodStarted, retryInformation);

        CustomRequest customRequestReplicas =
            new CustomRequest(new List<CustomHeader> { new() { Key = "key", Values = new[] { "value1" } } },
                new byte[1], "no-replicas", "/download", "GET", "");
        var jsonCustomNoReplicas = MemoryPackSerializer.Serialize(customRequestReplicas);
        await slimFaasQueue.EnqueueAsync("no-replicas", jsonCustomNoReplicas, retryInformation);

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            QueuesDelayMilliseconds = 10
        });

        SlimQueuesWorker service = new SlimQueuesWorker(
            slimFaasQueue,
            replicasService.Object,
            historyHttpService,
            logger.Object,
            serviceProvider.Object,
            slimDataStatus.Object,
            masterService.Object,
            workersOptions);
        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);

        await Task.Delay(3000);

        await cts.CancelAsync();
        await task;
        Assert.True(task.IsCompletedSuccessfully);
        sendClientMock.Verify(v => v.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(), It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<Proxy?>()),
            Times.Once());
    }

    [Fact]
    public async Task LogErrorWhenExceptionIsThrown()
    {
        Mock<IServiceProvider> serviceProvider = new Mock<IServiceProvider>();
        Mock<IReplicasService> replicasService = new Mock<IReplicasService>();
        replicasService.Setup(rs => rs.Deployments).Throws(new Exception());
        HistoryHttpMemoryService historyHttpService = new HistoryHttpMemoryService();
        Mock<ILogger<SlimQueuesWorker>> logger = new Mock<ILogger<SlimQueuesWorker>>();
        SlimFaasQueue redisQueue = new SlimFaasQueue(new DatabaseMockService());
        Mock<ISlimDataStatus> slimDataStatus = new Mock<ISlimDataStatus>();
        slimDataStatus.Setup(s => s.WaitForReadyAsync()).Returns(Task.CompletedTask);

        Mock<IMasterService> masterService = new Mock<IMasterService>();
        masterService.Setup(s => s.IsMaster).Returns(true);

        var workersOptions = Microsoft.Extensions.Options.Options.Create(new WorkersOptions
        {
            QueuesDelayMilliseconds = 10
        });

        SlimQueuesWorker service = new SlimQueuesWorker(
            redisQueue,
            replicasService.Object,
            historyHttpService,
            logger.Object,
            serviceProvider.Object,
            slimDataStatus.Object,
            masterService.Object,
            workersOptions);

        using var cts = new CancellationTokenSource();
        Task task = service.StartAsync(cts.Token);

        await Task.Delay(100);

        await cts.CancelAsync();
        await task;

        Assert.True(task.IsCompletedSuccessfully);
        logger.Verify(l => l.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()), Times.AtLeastOnce);
    }
}
