using System.Net.WebSockets;
using MemoryPack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Workers;

public sealed class WebSocketAsyncQueueWorkerTests
{
    [Fact]
    public async Task Mutation_and_completion_signals_dispatch_and_callback_without_polling()
    {
        const string functionName = "websocket-function";
        var signal = new SlimDataQueueSignal();
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackObserved = new TaskCompletionSource<ListQueueItemStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        CustomRequest request = new([], [1], functionName, "/work", "POST", "");
        var item = new QueueData(
            "message-1",
            MemoryPackSerializer.Serialize(request),
            1,
            true,
            0,
            TimeSpan.FromSeconds(30).Ticks,
            "websocket-client");
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(service => service.GetDispatchStateAsync(functionName))
            .ReturnsAsync(new QueueDispatchState(1, 0, 0, []));
        queue.SetupSequence(service => service.DequeueAsync(
                functionName,
                It.IsAny<int>(),
                It.IsAny<IList<string>?>()))
            .ReturnsAsync([item])
            .ReturnsAsync([]);
        queue.Setup(service => service.ListCallbackAsync(functionName, It.IsAny<ListQueueItemStatus>()))
            .Callback<string, ListQueueItemStatus>((_, status) => callbackObserved.TrySetResult(status))
            .Returns(Task.CompletedTask);

        var socket = new Mock<System.Net.WebSockets.WebSocket>();
        socket.SetupGet(value => value.State).Returns(WebSocketState.Open);
        var registry = new WebSocketConnectionRegistry(Mock.Of<ILogger<WebSocketConnectionRegistry>>());
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(service => service.Deployments).Returns(new DeploymentsInformations(
            [],
            new SlimFaasDeploymentInformation(1, []),
            []));
        (bool registered, string? error) = registry.TryRegister(
            new WebSocketClientConnection
            {
                FunctionName = functionName,
                Socket = socket.Object,
                Configuration = new WebSocketFunctionConfiguration
                {
                    NumberParallelRequest = 1,
                    NumberParallelRequestPerPod = 1
                }
            },
            replicas.Object);
        Assert.True(registered, error);

        var sendClient = new Mock<IWebSocketSendClient>();
        sendClient.Setup(service => service.SendAsync(
                functionName,
                It.IsAny<CustomRequest>(),
                "message-1",
                It.IsAny<bool>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => sendStarted.TrySetResult())
            .Returns(response.Task);
        var repository = new Mock<IWebSocketFunctionRepository>();
        repository.Setup(service => service.GetVirtualDeployments()).Returns(
        [
            new DeploymentInformation(
                Replicas: 1,
                Deployment: functionName,
                Namespace: "websocket-virtual",
                NumberParallelRequest: 1,
                ReplicasMin: 0,
                ReplicasAtStart: 1,
                TimeoutSecondBeforeSetReplicasMin: 0,
                ReplicasStartAsSoonAsOneFunctionRetrieveARequest: false,
                Configuration: new SlimFaasConfiguration(),
                Pods: [new PodInformation("ws", true, true, "websocket-client", functionName)],
                EndpointReady: true,
                NumberParallelRequestPerPod: 1)
        ]);
        var master = new Mock<IMasterService>();
        master.SetupGet(service => service.IsMaster).Returns(true);
        var worker = new WebSocketQueuesWorker(
            queue.Object,
            registry,
            sendClient.Object,
            repository.Object,
            new HistoryHttpMemoryService(),
            Mock.Of<ILogger<WebSocketQueuesWorker>>(),
            master.Object,
            Microsoft.Extensions.Options.Options.Create(
                new WorkersOptions { QueuesDelayMilliseconds = 60_000 }),
            signal);

        // The pulse intentionally occurs before StartAsync to cover the startup race.
        signal.Pulse();
        await worker.StartAsync(CancellationToken.None);
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        response.TrySetResult(StatusCodes.Status204NoContent);
        ListQueueItemStatus callback = await callbackObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(callback.Items);
        QueueItemStatus status = Assert.Single(callback.Items);
        Assert.Equal("message-1", status.Id);
        Assert.Equal(StatusCodes.Status204NoContent, status.HttpCode);
        queue.Verify(service => service.GetDispatchStateAsync(functionName), Times.AtLeastOnce);
        queue.Verify(service => service.CountElementAsync(
            It.IsAny<string>(), It.IsAny<IList<CountType>>(), It.IsAny<int>()), Times.Never);
        queue.Verify(service => service.ListElementsAsync(
            It.IsAny<string>(), It.IsAny<IList<CountType>>(), It.IsAny<int>()), Times.Never);
    }
}
