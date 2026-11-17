using System.Net;
using System.Collections.Immutable;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas.DeadLetters;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Workers;

[Collection("SlimQueuesWorkerOffload")]
public sealed class AsyncQueueWorkerTests
{
    public AsyncQueueWorkerTests() => Proxy.IpAddresses.Clear();

    [Theory]
    [InlineData("zero")]
    [InlineData("not-ready")]
    [InlineData("expired")]
    public async Task Held_retry_respects_function_readiness_and_the_existing_reservation_deadline(string mode)
    {
        var original = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dequeueEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDequeue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterDequeue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        var pod = new PodInformation("pod", true, true, "10.0.0.42", "function");
        var function = new DeploymentInformation("function", "default", [pod], new SlimFaasConfiguration(), 1,
            NumberParallelRequest: 2, NumberParallelRequestPerPod: 2, EndpointReady: true);
        var readySnapshot = new DeploymentsInformations([function], new SlimFaasDeploymentInformation(1, []), []);
        var snapshot = readySnapshot;
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(value => value.Deployments).Returns(() => Volatile.Read(ref snapshot));
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData("item", MemoryPackSerializer.Serialize(request), 0, false,
            mode == "expired" ? DateTime.UtcNow.AddSeconds(-20).Ticks : DateTime.UtcNow.Ticks,
            TimeSpan.FromSeconds(8).Ticks, "10.0.0.42");
        int snapshots = 0, dequeues = 0, sends = 0, done = 0;
        var callbackCodes = new List<int>();
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).Returns(() =>
        {
            int count = Interlocked.Increment(ref snapshots);
            if (count >= 3) afterDequeue.TrySetResult();
            if (count >= 4) afterCompletion.TrySetResult();
            return Task.FromResult(Volatile.Read(ref done) != 0 || (mode == "expired" && original.Task.IsCompleted)
                ? QueueDispatchState.Empty : count == 1 ? new QueueDispatchState(1, 0, 0, [])
                : new QueueDispatchState(1, 1, 0, [new("item", "10.0.0.42")]));
        });
        queue.Setup(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .Returns(async () =>
            {
                int count = Interlocked.Increment(ref dequeues);
                if (count == 1) return [message];
                if (count > 2) return [];
                dequeueEntered.TrySetResult();
                await releaseDequeue.Task;
                return [message with { TryNumber = 1, IsLastTry = true,
                    LastRetryTimeTicks = mode == "expired" ? DateTime.UtcNow.AddSeconds(-9).Ticks : DateTime.UtcNow.Ticks }];
            });
        queue.Setup(value => value.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Callback<string, ListQueueItemStatus>((_, statuses) =>
            {
                callbackCodes.AddRange(statuses.Items!.Select(item => item.HttpCode));
                Volatile.Write(ref done, 1);
                acknowledged.TrySetResult();
            }).Returns(Task.CompletedTask);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(() =>
            {
                int count = Interlocked.Increment(ref sends);
                sent.TrySetResult();
                return count == 1 ? original.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            readyObserved: ready, parallelism: 2, replicasService: replicas.Object);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); signal.Pulse();
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5)); signal.Pulse();
            await dequeueEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseDequeue.SetResult(); signal.Pulse();
            await afterDequeue.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (mode != "expired")
                Volatile.Write(ref snapshot, readySnapshot with { Functions = [mode == "zero"
                    ? function with { Replicas = 0, Pods = [], EndpointReady = false }
                    : function with { Pods = [pod with { Ready = false }], EndpointReady = false }] });
            original.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            await afterCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref sends));
            Assert.Empty(callbackCodes);
            if (mode != "expired")
            {
                Volatile.Write(ref snapshot, readySnapshot); signal.Pulse();
                await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, Volatile.Read(ref sends));
                Assert.Equal([200], callbackCodes);
            }
        }
        finally
        {
            releaseDequeue.TrySetResult();
            original.TrySetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(202, false)]
    [InlineData(500, true)]
    [InlineData(202, true)]
    public async Task A_reserved_retry_waits_for_the_owned_exchange_and_keeps_its_attempt_after_a_nonterminal_result(int firstStatus, bool loseLeadership)
    {
        var original = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dequeueEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDequeue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterDequeue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followerObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData("item", MemoryPackSerializer.Serialize(request), 0, false, 0,
            TimeSpan.FromSeconds(8).Ticks, "10.0.0.42");
        int snapshots = 0, dequeues = 0, sends = 0, done = 0, master = 1;
        var attempts = new List<string>();
        var callbackCodes = new List<int>();
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).Returns(() =>
        {
            int snapshot = Interlocked.Increment(ref snapshots);
            if (snapshot >= 3)
                afterDequeue.TrySetResult();
            return Task.FromResult(Volatile.Read(ref done) != 0 ? QueueDispatchState.Empty
                : snapshot == 1 ? new QueueDispatchState(1, 0, 0, [])
                : new QueueDispatchState(1, 1, 0, [new("item", "10.0.0.42")]));
        });
        queue.Setup(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .Returns(async () =>
            {
                int dequeue = Interlocked.Increment(ref dequeues);
                if (dequeue == 1)
                    return [message];
                if (dequeue > 2)
                    return [];
                dequeueEntered.TrySetResult();
                await releaseDequeue.Task;
                return [message with { TryNumber = 1, IsLastTry = true }];
            });
        queue.Setup(value => value.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Callback<string, ListQueueItemStatus>((_, statuses) =>
            {
                callbackCodes.AddRange(statuses.Items!.Select(item => item.HttpCode));
                Volatile.Write(ref done, 1);
                acknowledged.TrySetResult();
            }).Returns(Task.CompletedTask);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns<CustomRequest, SlimFaasDefaultConfiguration, string?, CancellationTokenSource?, IProxy?, string?, string?, string?, Stream?, string?, string?>(
                (current, _, _, _, _, _, _, _, _, _, _) =>
                {
                    attempts.Add(Assert.Single(current.Headers, header => header.Key == SlimQueuesWorker.SlimfaasTryNumber).Values[0]!);
                    int send = Interlocked.Increment(ref sends);
                    sent.TrySetResult();
                    return send == 1 ? original.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                });
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            isMaster: () =>
            {
                bool leader = Volatile.Read(ref master) != 0;
                if (!leader)
                    followerObserved.TrySetResult();
                return leader;
            }, readyObserved: ready, parallelism: 2);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await dequeueEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            releaseDequeue.SetResult();
            signal.Pulse();
            await afterDequeue.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref sends));
            Assert.Empty(callbackCodes);

            if (loseLeadership)
                Volatile.Write(ref master, 0);
            original.SetResult(new HttpResponseMessage((HttpStatusCode)firstStatus));
            if (loseLeadership)
            {
                signal.Pulse();
                await followerObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await worker.StopAsync(CancellationToken.None);
                Assert.Equal(1, Volatile.Read(ref sends));
                Assert.Empty(callbackCodes);
                return;
            }
            await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref sends));
            Assert.Equal(["0", "1"], attempts);
            Assert.Equal([200], callbackCodes);
        }
        finally
        {
            releaseDequeue.TrySetResult();
            original.TrySetResult(new HttpResponseMessage((HttpStatusCode)firstStatus));
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reservation_expiring_after_snapshot_does_not_replace_a_locally_owned_delivery(bool completedDuringDequeue)
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dequeueEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDequeue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData("item", MemoryPackSerializer.Serialize(request), 0, false, 0,
            TimeSpan.FromSeconds(8).Ticks, "10.0.0.42");
        int snapshots = 0, dequeues = 0, sends = 0;
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).Returns(() =>
        {
            int snapshot = Interlocked.Increment(ref snapshots);
            if (snapshot > 2)
                nextCycle.TrySetResult();
            return Task.FromResult(snapshot == 1 ? new QueueDispatchState(1, 0, 0, [])
                : new QueueDispatchState(snapshot == 2 ? 1 : 0, 1, 0, [new("item", "10.0.0.42")]));
        });
        queue.Setup(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref dequeues) == 1)
                    return [message];
                // The earlier snapshot still reported item as Running. A later
                // dequeue timestamp can see its reservation expired and return
                // a retry of that same ID while the local HTTP exchange is owned.
                dequeueEntered.TrySetResult();
                await releaseDequeue.Task;
                return [message with { TryNumber = 1 }];
            });
        queue.Setup(value => value.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(() =>
            {
                int send = Interlocked.Increment(ref sends);
                sent.TrySetResult();
                return send == 1 ? response.Task : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            });
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            readyObserved: ready, parallelism: 2);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await dequeueEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (completedDuringDequeue)
            {
                long version = signal.Version;
                response.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                Assert.True(await signal.WaitForChangeAsync(version, TimeSpan.FromSeconds(5), CancellationToken.None) > version);
            }
            releaseDequeue.SetResult();
            signal.Pulse();
            await nextCycle.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref sends));
        }
        finally
        {
            releaseDequeue.TrySetResult();
            response.TrySetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_snapshot_does_not_redispatch_a_locally_owned_delivery(bool completedDuringSnapshot)
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource<QueueDispatchState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextCycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData("item", MemoryPackSerializer.Serialize(request), 0, false, 0,
            TimeSpan.FromSeconds(8).Ticks, "10.0.0.42");
        int snapshots = 0;
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).Returns(async () =>
        {
            int snapshot = Interlocked.Increment(ref snapshots);
            if (snapshot == 2)
            {
                snapshotEntered.TrySetResult();
                return await releaseSnapshot.Task;
            }
            if (snapshot > 2)
            {
                nextCycle.TrySetResult();
                return QueueDispatchState.Empty;
            }
            return new QueueDispatchState(1, 0, 0, []);
        });
        queue.Setup(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([message]);
        queue.Setup(value => value.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Returns(Task.CompletedTask);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(() => { sent.TrySetResult(); return response.Task; });
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            readyObserved: ready, parallelism: 2);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (completedDuringSnapshot)
            {
                long version = signal.Version;
                response.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                Assert.True(await signal.WaitForChangeAsync(version, TimeSpan.FromSeconds(5), CancellationToken.None) > version);
            }
            // The Raft view now considers this same ID available again, while
            // this worker still owns its outbound request or completed receipt.
            releaseSnapshot.SetResult(new QueueDispatchState(1, 0, 0, []));
            signal.Pulse();
            await nextCycle.Task.WaitAsync(TimeSpan.FromSeconds(5));

            queue.Verify(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()), Times.Once);
            client.Verify(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        }
        finally
        {
            releaseSnapshot.TrySetResult(QueueDispatchState.Empty);
            response.TrySetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(200)]
    [InlineData(500)]
    [InlineData(202)]
    public async Task Queue_completion_retains_dispatch_identity_and_waits_for_callback(int statusCode)
    {
        var tracker = new NetworkActivityTracker();
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runningObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData("item", MemoryPackSerializer.Serialize(request), 1, true, 0,
            TimeSpan.FromSeconds(30).Ticks, "10.0.0.42");
        int completed = 0;
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(q => q.GetDispatchStateAsync("function")).Returns(() =>
        {
            if (Volatile.Read(ref completed) != 0) return Task.FromResult(QueueDispatchState.Empty);
            if (response.Task.IsCompleted) runningObserved.TrySetResult();
            return Task.FromResult(new QueueDispatchState(1, 1, 0, [new("item", "10.0.0.42")]));
        });
        queue.SetupSequence(q => q.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([message]).ReturnsAsync([]);
        queue.Setup(q => q.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Callback(() => Interlocked.Exchange(ref completed, 1)).Returns(Task.CompletedTask);
        var client = new Mock<ISendClient>();
        client.Setup(c => c.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Callback<CustomRequest, SlimFaasDefaultConfiguration, string?, CancellationTokenSource?, IProxy?, string?, string?, string?, Stream?, string?, string?>(
                (_, _, _, _, _, _, _, _, _, _, correlation) => sent.TrySetResult(correlation!))
            .Returns(response.Task);
        var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            readyObserved: readyObserved, tracker: tracker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await readyObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            string dispatchId = await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(dispatchId, Assert.Single(tracker.GetRecent()).Id);
            response.SetResult(new HttpResponseMessage((HttpStatusCode)statusCode));
            if (statusCode == 202)
            {
                await runningObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.DoesNotContain(tracker.GetRecent(), e => e.Type == "request_end");
                queue.Verify(q => q.ListCallbackAsync(It.IsAny<string>(), It.IsAny<ListQueueItemStatus>()), Times.Never);
                Interlocked.Exchange(ref completed, 1);
                signal.Pulse();
            }
            await WaitUntilAsync(() => tracker.GetRecent().Any(e => e.Type == "request_end"));
            var end = Assert.Single(tracker.GetRecent(), e => e.Type == "request_end");
            Assert.Equal(dispatchId, end.CorrelationId);
            Assert.Equal("function", end.Source);
            Assert.Equal("function", end.QueueName);
            Assert.Equal("10.0.0.42", end.SourcePod);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

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
                It.IsAny<Stream?>(), It.IsAny<string?>(), activityCorrelationId: It.IsAny<string?>()))
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
    public async Task Managed_completion_retries_persistence_without_redispatching_application()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        var database = new Mock<IDatabaseService>();
        int calls = 0;
        database.Setup(value => value.ExecuteDeadLetterAsync(It.Is<DeadLetterMutation>(
            mutation => mutation.Action == DeadLetterAction.Complete && mutation.ElementId == "item")))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    failed.TrySetResult();
                    return Task.FromException<DeadLetterMutationResult>(new IOException("Persistence unavailable"));
                }
                committed.TrySetResult();
                return Task.FromResult(new DeadLetterMutationResult());
            });
        var payload = new SlimDataPayload
        {
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(DeadLetterKeys.Policy("function"),
                MemoryPackSerializer.Serialize(new DeadLetterPolicy { Enabled = true })),
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };
        var state = new Mock<ISupplier<SlimDataPayload>>();
        state.Setup(value => value.Invoke()).Returns(payload);
        var tracker = new NetworkActivityTracker();
        using var deadLetters = new DeadLetterService(database.Object, state.Object, Mock.Of<IReplicasService>(),
            Mock.Of<IWebSocketFunctionRepository>(), Mock.Of<IFunctionAccessPolicy>(),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()), Mock.Of<IClusterFileSync>(),
            Mock.Of<IMasterService>(), tracker, Mock.Of<ILogger<DeadLetterService>>());
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).ReturnsAsync(new QueueDispatchState(1, 0, 0, []));
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        queue.SetupSequence(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([new QueueData("item", MemoryPackSerializer.Serialize(request), 1, true, 0,
                TimeSpan.FromSeconds(30).Ticks, "10.0.0.42")]).ReturnsAsync([]);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.BadRequest));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            readyObserved: ready, tracker: tracker, deadLetters: deadLetters);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(tracker.GetRecent(), item => item.Type == "request_end");
            signal.Pulse();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => tracker.GetRecent().Any(item => item.Type == "request_end"));
            client.Verify(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
            Assert.Equal(2, calls);
            queue.Verify(value => value.ListCallbackAsync(It.IsAny<string>(), It.IsAny<ListQueueItemStatus>()), Times.Never);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Completed_delivery_is_committed_before_leadership_loss_clears_tracking()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSnapshot = new TaskCompletionSource<QueueDispatchState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource<ListQueueItemStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        int master = 1, snapshots = 0;
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).Returns(async () =>
        {
            if (Interlocked.Increment(ref snapshots) == 2)
            {
                snapshotEntered.TrySetResult();
                return await releaseSnapshot.Task;
            }
            return new QueueDispatchState(1, 0, 0, []);
        });
        queue.SetupSequence(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([new QueueData("item", MemoryPackSerializer.Serialize(request), 1, true, 0,
                TimeSpan.FromSeconds(30).Ticks, "10.0.0.42")]).ReturnsAsync([]);
        queue.Setup(value => value.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>()))
            .Callback<string, ListQueueItemStatus>((_, statuses) => committed.TrySetResult(statuses))
            .Returns(Task.CompletedTask);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns(() => { sent.TrySetResult(); return response.Task; });
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            () => Volatile.Read(ref master) != 0, ready);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await snapshotEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long version = signal.Version;
            response.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            Assert.True(await signal.WaitForChangeAsync(version, TimeSpan.FromSeconds(5), CancellationToken.None) > version);
            Volatile.Write(ref master, 0);
            releaseSnapshot.SetResult(QueueDispatchState.Empty);
            ListQueueItemStatus callback = await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(callback.Items);
            Assert.Equal(204, Assert.Single(callback.Items).HttpCode);
            Assert.Equal("item", Assert.Single(callback.Items).Id);
            queue.Verify(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()), Times.Once);
        }
        finally
        {
            releaseSnapshot.TrySetResult(QueueDispatchState.Empty);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Legacy_completion_retries_persistence_without_redispatching_application()
    {
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        var tracker = new NetworkActivityTracker();
        var queue = new Mock<ISlimFaasQueue>();
        int callbacks = 0;
        queue.Setup(value => value.GetDispatchStateAsync("function")).ReturnsAsync(new QueueDispatchState(1, 0, 0, []));
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        queue.SetupSequence(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([new QueueData("item", MemoryPackSerializer.Serialize(request), 1, true, 0,
                TimeSpan.FromSeconds(30).Ticks, "10.0.0.42")]).ReturnsAsync([]);
        queue.Setup(value => value.ListCallbackAsync("function", It.IsAny<ListQueueItemStatus>())).Returns(() =>
        {
            if (Interlocked.Increment(ref callbacks) == 1)
            {
                failed.TrySetResult();
                return Task.FromException(new IOException("Callback persistence unavailable"));
            }
            committed.TrySetResult();
            return Task.CompletedTask;
        });
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
            It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.NoContent));
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            readyObserved: ready, tracker: tracker);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(tracker.GetRecent(), item => item.Type == "request_end");
            signal.Pulse();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => tracker.GetRecent().Any(item => item.Type == "request_end"));
            Assert.Equal(2, callbacks);
            client.Verify(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Once);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Stale_completion_cannot_remove_a_replacement_dispatch_with_the_same_id()
    {
        var oldResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource<CancellationTokenSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var signal = new SlimDataQueueSignal();
        int master = 1, sends = 0, staleSnapshots = 0;
        var queue = new Mock<ISlimFaasQueue>();
        queue.Setup(value => value.GetDispatchStateAsync("function")).Returns(() =>
        {
            if (oldResponse.Task.IsCompleted)
            {
                if (Interlocked.Increment(ref staleSnapshots) == 1) signal.Pulse();
                else settled.TrySetResult();
            }
            return Task.FromResult(new QueueDispatchState(1, 0, 0, []));
        });
        CustomRequest request = new([], [1], "function", "/work", "POST", "");
        var message = new QueueData("item", MemoryPackSerializer.Serialize(request), 1, true, 0,
            TimeSpan.FromSeconds(30).Ticks, "10.0.0.42");
        queue.SetupSequence(value => value.DequeueAsync("function", It.IsAny<int>(), It.IsAny<IList<string>?>()))
            .ReturnsAsync([message]).ReturnsAsync([message]).ReturnsAsync([message with { Id = "other" }]).ReturnsAsync([]);
        var client = new Mock<ISendClient>();
        client.Setup(value => value.SendHttpRequestAsync(It.IsAny<CustomRequest>(), It.IsAny<SlimFaasDefaultConfiguration>(),
                It.IsAny<string?>(), It.IsAny<CancellationTokenSource?>(), It.IsAny<IProxy?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<Stream?>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .Returns<CustomRequest, SlimFaasDefaultConfiguration, string?, CancellationTokenSource?, IProxy?, string?, string?, string?, Stream?, string?, string?>(
                (_, _, _, cancellation, _, _, _, _, _, _, _) =>
                {
                    int call = Interlocked.Increment(ref sends);
                    if (call == 1) oldStarted.TrySetResult(cancellation!);
                    else replacementStarted.TrySetResult();
                    return call == 1 ? oldResponse.Task : replacementResponse.Task;
                });
        using var worker = BuildWorker(queue.Object, signal, Task.CompletedTask, client.Object,
            () => Volatile.Read(ref master) != 0, ready);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signal.Pulse();
            CancellationTokenSource cancellation = await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Volatile.Write(ref master, 0);
            signal.Pulse();
            await WaitUntilAsync(() => cancellation.IsCancellationRequested);
            Volatile.Write(ref master, 1);
            signal.Pulse();
            await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldResponse.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, Volatile.Read(ref sends));
            queue.Verify(value => value.ListCallbackAsync(It.IsAny<string>(), It.IsAny<ListQueueItemStatus>()), Times.Never);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            oldResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            replacementResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
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
                It.IsAny<Stream?>(), It.IsAny<string?>(), activityCorrelationId: It.IsAny<string?>()))
            .Callback<CustomRequest, SlimFaasDefaultConfiguration, string?, CancellationTokenSource?, IProxy?, string?, string?, string?, Stream?, string?, string?>(
                (_, _, _, cancellation, _, _, _, _, _, _, _) => sendStarted.TrySetResult(cancellation!))
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
        TaskCompletionSource? readyObserved = null,
        NetworkActivityTracker? tracker = null,
        DeadLetterService? deadLetters = null,
        int parallelism = 1,
        IReplicasService? replicasService = null)
    {
        var replicas = new Mock<IReplicasService>();
        replicas.Setup(service => service.Deployments).Returns(new DeploymentsInformations(
            Functions:
            [
                new DeploymentInformation(
                    Replicas: 1,
                    Deployment: "function",
                    Namespace: "default",
                    NumberParallelRequest: parallelism,
                    ReplicasMin: 0,
                    ReplicasAtStart: 1,
                    TimeoutSecondBeforeSetReplicasMin: 300,
                    ReplicasStartAsSoonAsOneFunctionRetrieveARequest: true,
                    Configuration: new SlimFaasConfiguration
                    {
                        DefaultAsync = new() { DeadLetterQueue = new(Enabled: deadLetters is not null) }
                    },
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
            replicasService ?? replicas.Object,
            new HistoryHttpMemoryService(),
            Mock.Of<ILogger<SlimQueuesWorker>>(),
            sendClient ?? Mock.Of<ISendClient>(),
            status.Object,
            master.Object,
            Mock.Of<IClusterFileSync>(),
            Mock.Of<IDatabaseService>(),
            Microsoft.Extensions.Options.Options.Create(
                new WorkersOptions { QueuesDelayMilliseconds = 60_000 }),
            tracker ?? new NetworkActivityTracker(),
            signal,
            deadLetters);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }
}
