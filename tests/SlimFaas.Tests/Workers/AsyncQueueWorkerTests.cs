using System.Net;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Workers;

[Collection("SlimQueuesWorkerOffload")]
public sealed class AsyncQueueWorkerTests
{
    public AsyncQueueWorkerTests() => Proxy.IpAddresses.Clear();

    [Fact]
    public async Task Durable_signal_wakes_worker_and_uses_one_lightweight_snapshot()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(service => service.GetDispatchStateAsync("function"))
            .Callback(() => snapshotObserved.TrySetResult())
            .ReturnsAsync(QueueDispatchState.Empty);
        var signal = new SlimDataQueueSignal();
        SlimQueuesWorker worker = BuildWorker(queue.Object, signal, ready.Task, readyObserved: readyObserved);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await readyObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        signal.Pulse();
        ready.TrySetResult();
        await snapshotObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        queue.Verify(service => service.GetDispatchStateAsync("function"), Times.AtLeastOnce);
        queue.Verify(service => service.CountElementAsync(
            It.IsAny<string>(), It.IsAny<IList<CountType>>(), It.IsAny<int>()), Times.Never);
        queue.Verify(service => service.ListElementsAsync(
            It.IsAny<string>(), It.IsAny<IList<CountType>>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Completion_mailbox_wakes_worker_and_batches_callback_without_poll_delay()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackObserved = new TaskCompletionSource<ListQueueItemStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        var readyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData(
            "message-1",
            MemoryPackSerializer.Serialize(request),
            1,
            true,
            0,
            TimeSpan.FromSeconds(30).Ticks,
            "10.0.0.42");
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(service => service.GetDispatchStateAsync("function"))
            .ReturnsAsync(new QueueDispatchState(1, 0, 0, []));
        queue.SetupSequence(service => service.DequeueAsync(
                "function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([message])
            .ReturnsAsync([]);
        queue.Setup(service => service.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Callback<string, ListQueueItemStatus>((_, status) => callbackObserved.TrySetResult(status))
            .Returns(Task.CompletedTask);
        var sendClient = new Mock<ISendClient>();
        sendClient.Setup(service => service.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .Callback(() => sendStarted.TrySetResult())
            .Returns(response.Task);
        SlimQueuesWorker worker = BuildWorker(
            queue.Object,
            signal,
            Task.CompletedTask,
            sendClient.Object,
            readyObserved: readyObserved);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await readyObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        signal.Pulse();
        await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        response.TrySetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        ListQueueItemStatus callback = await callbackObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(callback.Items);
        QueueItemStatus status = Assert.Single(callback.Items);
        Assert.Equal("message-1", status.Id);
        Assert.Equal((int)HttpStatusCode.NoContent, status.HttpCode);
        queue.Verify(service => service.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()), Times.Once);
    }

    [Fact]
    public async Task Leadership_loss_cancels_long_running_dispatch()
    {
        var sendStarted = new TaskCompletionSource<CancellationTokenSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        var readyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isMaster = true;
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData(
            "message-1",
            MemoryPackSerializer.Serialize(request),
            1,
            true,
            0,
            TimeSpan.FromSeconds(30).Ticks,
            "10.0.0.43");
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(service => service.GetDispatchStateAsync("function"))
            .ReturnsAsync(new QueueDispatchState(1, 0, 0, []));
        queue.SetupSequence(service => service.DequeueAsync(
                "function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([message])
            .ReturnsAsync([]);
        var sendClient = new Mock<ISendClient>();
        sendClient.Setup(service => service.SendHttpRequestAsync(
                It.IsAny<CustomRequest>(),
                It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationTokenSource?>(),
                It.IsAny<IProxy?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<Stream?>()))
            .Callback<CustomRequest, SlimFaasDefaultConfiguration, string?, CancellationTokenSource?, IProxy?, string?, string?, string?, Stream?>(
                (_, _, _, cancellation, _, _, _, _, _) => sendStarted.TrySetResult(cancellation!))
            .Returns(neverCompletes.Task);
        SlimQueuesWorker worker = BuildWorker(
            queue.Object,
            signal,
            Task.CompletedTask,
            sendClient.Object,
            () => isMaster,
            readyObserved);
        using var stopping = new CancellationTokenSource();

        await worker.StartAsync(stopping.Token);
        await readyObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        signal.Pulse();
        CancellationTokenSource requestCancellation = await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        isMaster = false;
        signal.Pulse();
        await WaitUntilAsync(() => requestCancellation.IsCancellationRequested);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(requestCancellation.IsCancellationRequested);
    }

    private static SlimQueuesWorker BuildWorker(
        ISlimFaasQueue queue,
        SlimDataQueueSignal signal,
        Task ready,
        ISendClient? sendClient = null,
        Func<bool>? isMaster = null,
        TaskCompletionSource? readyObserved = null)
    {
        var replicas = new Mock<IReplicasService>();
        replicas.Setup(service => service.Deployments).Returns(new DeploymentsInformations(
            Functions:
            [
                new DeploymentInformation(
                    Replicas: 1,
                    Deployment: "function",
                    Namespace: "default",
                    NumberParallelRequest: 1,
                    ReplicasMin: 0,
                    ReplicasAtStart: 1,
                    TimeoutSecondBeforeSetReplicasMin: 300,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true,
                    Configuration: new SlimFaasConfiguration(),
                    Pods: [new PodInformation("pod", true, true, "10.0.0.42", "function")],
                    EndpointReady: true)
            ],
            SlimFaas: new SlimFaasDeploymentInformation(1, []),
            Pods: []));
        var status = new Mock<ISlimDataStatus>();
        status.Setup(service => service.WaitForReadyAsync())
            .Callback(() => readyObserved?.TrySetResult())
            .Returns(ready);
        var master = new Mock<IMasterService>();
        master.SetupGet(service => service.IsMaster).Returns(() => isMaster?.Invoke() ?? true);
        return new SlimQueuesWorker(
            queue,
            replicas.Object,
            new HistoryHttpMemoryService(),
            Mock.Of<ILogger<SlimQueuesWorker>>(),
            sendClient ?? Mock.Of<ISendClient>(),
            status.Object,
            master.Object,
            Mock.Of<IClusterFileSync>(),
            Mock.Of<IDatabaseService>(),
            Microsoft.Extensions.Options.Options.Create(
                new WorkersOptions { QueuesDelayMilliseconds = 60_000 }),
            new NetworkActivityTracker(),
            signal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }
}
