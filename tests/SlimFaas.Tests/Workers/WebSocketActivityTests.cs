using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;
using MessageType = SlimFaas.WebSocket.WebSocketMessageType;

namespace SlimFaas.Tests.Workers;

public sealed class WebSocketActivityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_an_active_frame_send_aborts_the_ambiguous_transport(bool binary)
    {
        var (client, connection, _) = Setup();
        using (connection)
        using (var cancellation = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Mock<System.Net.WebSockets.WebSocket> socket = Mock.Get(connection.Socket);
            socket.Setup(value => value.SendAsync(It.IsAny<ArraySegment<byte>>(),
                    It.IsAny<System.Net.WebSockets.WebSocketMessageType>(), true, It.IsAny<CancellationToken>()))
                .Returns<ArraySegment<byte>, System.Net.WebSockets.WebSocketMessageType, bool, CancellationToken>(
                    (_, _, _, token) => { entered.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
            socket.Setup(value => value.Abort()).Callback(() =>
                socket.SetupGet(value => value.State).Returns(WebSocketState.Aborted));
            Task pending = binary
                ? connection.SendBinaryAsync([1], cancellation.Token)
                : client.SendAsync("ws-function", new CustomRequest([], [], "ws-function", "/", "POST", ""),
                    "ambiguous", false, 0, cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await cancellation.CancelAsync();
            if (binary)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else
                Assert.Equal(504, await (Task<int>)pending);

            socket.Verify(value => value.Abort(), Times.Once);
            Assert.Empty(connection.PendingCallbacks);
        }
    }

    [Fact]
    public async Task Async_cancellation_while_waiting_for_the_send_lock_does_not_fence_an_unsent_id()
    {
        var (client, connection, _) = Setup();
        using (connection)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Mock.Get(connection.Socket).Setup(socket => socket.SendAsync(It.IsAny<ArraySegment<byte>>(),
                    System.Net.WebSockets.WebSocketMessageType.Binary, true, It.IsAny<CancellationToken>()))
                .Returns(() => { entered.TrySetResult(); return release.Task; });
            Task occupying = connection.SendBinaryAsync([1], CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                using var cancellation = new CancellationTokenSource();
                Task<int> first = client.SendAsync("ws-function",
                    new CustomRequest([], [], "ws-function", "/", "POST", ""), "unsent", false, 0, cancellation.Token);
                await cancellation.CancelAsync();

                Assert.Equal(504, await first);
                Assert.Empty(connection.PendingCallbacks);
                Mock.Get(connection.Socket).Verify(socket => socket.SendAsync(It.IsAny<ArraySegment<byte>>(),
                    System.Net.WebSockets.WebSocketMessageType.Text, true, It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                release.TrySetResult();
                await occupying;
            }

            Task<int> retry = client.SendAsync("ws-function",
                new CustomRequest([], [], "ws-function", "/", "POST", ""), "unsent", false, 1);
            Assert.Single(connection.PendingCallbacks).Value.TrySetResult(200);
            Assert.Equal(200, await retry);
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3600, true)]
    public void Registration_validates_and_applies_async_dead_letter_configuration(int maxAge, bool expected)
    {
        var registry = new WebSocketConnectionRegistry(NullLogger<WebSocketConnectionRegistry>.Instance);
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(value => value.Deployments).Returns(new DeploymentsInformations([], new SlimFaasDeploymentInformation(1, []), []));
        var socket = new Mock<System.Net.WebSockets.WebSocket>();
        socket.SetupGet(value => value.State).Returns(WebSocketState.Open);
        using var connection = new WebSocketClientConnection
        {
            FunctionName = "billing", Socket = socket.Object, Configuration = new()
            {
                Configuration = JsonSerializer.Serialize(new SlimFaasConfiguration
                {
                    DefaultAsync = new()
                    {
                        HttpTimeout = 40,
                        DeadLetterQueue = new() { Enabled = true, MaxAgeSeconds = maxAge }
                    }
                }, SlimFaasConfigurationSerializerContext.Default.SlimFaasConfiguration)
            }
        };
        var result = registry.TryRegister(connection, replicas.Object);
        Assert.Equal(expected, result.Success);
        if (expected)
        {
            var function = Assert.Single(new WebSocketFunctionRepository(registry).GetVirtualDeployments());
            Assert.True(function.Configuration.DefaultAsync.DeadLetterQueue.Enabled);
            Assert.Equal(40, function.Configuration.DefaultAsync.HttpTimeout);
            Assert.Equal(maxAge, function.Configuration.DefaultAsync.DeadLetterQueue.MaxAgeSeconds);
        }
        else Assert.Contains("MaxAgeSeconds", result.Error, StringComparison.Ordinal);
    }

    private static (WebSocketSendClient Client, WebSocketClientConnection Connection, NetworkActivityTracker Tracker) Setup(bool fail = false, int replicas = 1)
    {
        var tracker = new NetworkActivityTracker();
        var registry = new WebSocketConnectionRegistry(NullLogger<WebSocketConnectionRegistry>.Instance);
        var deployments = new Mock<IReplicasService>();
        deployments.SetupGet(r => r.Deployments).Returns(new DeploymentsInformations([], new SlimFaasDeploymentInformation(1, []), []));
        WebSocketClientConnection? first = null;
        for (int i = 0; i < replicas; i++)
        {
            var socket = new Mock<System.Net.WebSockets.WebSocket>();
            socket.SetupGet(s => s.State).Returns(WebSocketState.Open);
            var connection = new WebSocketClientConnection { FunctionName = "ws-function", Socket = socket.Object };
            socket.Setup(s => s.SendAsync(It.IsAny<ArraySegment<byte>>(), It.IsAny<System.Net.WebSockets.WebSocketMessageType>(), true, It.IsAny<CancellationToken>()))
                .Returns<ArraySegment<byte>, System.Net.WebSockets.WebSocketMessageType, bool, CancellationToken>((bytes, type, _, _) => {
                    if (fail) return Task.FromException(new IOException("Socket failed"));
                    if (type == System.Net.WebSockets.WebSocketMessageType.Binary)
                    {
                        var frame = BinaryFrame.DecodeHeader(bytes.AsSpan());
                        if (frame.Type == MessageType.SyncRequestEnd)
                            connection.PendingSyncStreams[frame.CorrelationId].ResponseStartTcs.TrySetResult(new SyncResponseStartPayload { StatusCode = 200 });
                    }
                    return Task.CompletedTask;
                });
            var (registered, error) = registry.TryRegister(connection, deployments.Object);
            Assert.True(registered, error); first ??= connection;
        }
        return (new WebSocketSendClient(registry, NullLogger<WebSocketSendClient>.Instance, tracker), first!, tracker);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sync_dispatch_and_completion_keep_job_and_replica_identity(bool abort)
    {
        var (client, connection, tracker) = Setup();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const string job = "daily-report-slimfaas-job-123";
        var result = await client.SendSyncRequestStreamAsync("ws-function", "GET", "/hello", "", [], null, job, "incoming", cancellation.Token);
        var start = Assert.Single(tracker.GetRecent());
        Assert.Equal("incoming", start.CorrelationId);
        Assert.Equal("request_out", start.Type); Assert.Equal(job, start.SourcePod); Assert.Equal(connection.ConnectionId, start.TargetPod);
        if (abort)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(result.WaitForEnd);
        }
        else
        {
            Assert.Single(connection.PendingSyncStreams).Value.ResponseEndTcs.SetResult();
            await result.WaitForEnd(); await result.WaitForEnd();
        }
        Assert.Empty(connection.PendingSyncStreams);
        var end = Assert.Single(tracker.GetRecent(), e => e.Type == "request_end");
        Assert.Equal(start.Id, end.CorrelationId); Assert.Equal(job, end.SourcePod); Assert.Equal(connection.ConnectionId, end.TargetPod);
    }

    [Fact]
    public async Task Failed_sync_send_completes_activity_once()
    {
        var (client, connection, tracker) = Setup(fail: true);
        await Assert.ThrowsAsync<IOException>(() => client.SendSyncRequestStreamAsync("ws-function", "GET", "/", "", [], null));
        Assert.Empty(connection.PendingSyncStreams);
        Assert.Single(tracker.GetRecent(), e => e.Type == "request_out");
        Assert.Single(tracker.GetRecent(), e => e.Type == "request_end");
    }

    [Fact]
    public async Task Async_dispatch_pairs_dequeue_with_the_selected_connection_and_a_retry_gets_a_new_identity()
    {
        var (client, connection, tracker) = Setup();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var pending = client.SendAsync("ws-function", new CustomRequest([], [], "ws-function", "/", "POST", ""), "element", false, attempt);
            Assert.Single(connection.PendingCallbacks).Value.TrySetResult(200);
            Assert.Equal(200, await pending);
        }
        var events = tracker.GetRecent();
        var dequeue = events.Where(e => e.Type == "dequeue").ToArray();
        var dispatch = events.Where(e => e.Type == "request_out").ToArray();
        Assert.Equal(2, dequeue.Length); Assert.Equal(2, dispatch.Length);
        Assert.NotEqual(dequeue[0].Id, dequeue[1].Id);
        foreach (var outgoing in dispatch)
        {
            Assert.Contains(dequeue, e => e.Id == outgoing.CorrelationId);
            Assert.Equal("ws-function", outgoing.QueueName);
            Assert.Equal(connection.ConnectionId, outgoing.TargetPod);
        }
    }

    [Fact]
    public async Task Publication_fanout_emits_one_event_for_each_replica_with_the_job_source()
    {
        var (client, _, tracker) = Setup(replicas: 3);
        const string job = "daily-report-slimfaas-job-123";
        await client.PublishEventAsync("ws-function", new CustomRequest([], [], "ws-function", "/", "POST", ""), "report", job, "publication");
        var events = tracker.GetRecent();
        Assert.Equal(3, events.Count); Assert.Equal(3, events.Select(e => e.TargetPod).Distinct().Count());
        Assert.All(events, e => { Assert.Equal("publication", e.CorrelationId); Assert.Equal("event_publish", e.Type); Assert.Equal(job, e.SourcePod); Assert.Equal("ws-function", e.Target); });
    }
}
