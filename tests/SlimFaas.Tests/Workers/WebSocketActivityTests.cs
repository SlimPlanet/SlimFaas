using System.Net.WebSockets;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;
using MessageType = SlimFaas.WebSocket.WebSocketMessageType;

namespace SlimFaas.Tests.Workers;

public sealed class WebSocketActivityTests
{
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
        var result = await client.SendSyncRequestStreamAsync("ws-function", "GET", "/hello", "", [], null, cancellation.Token, job);
        var start = Assert.Single(tracker.GetRecent());
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
    public async Task Publication_fanout_emits_one_event_for_each_replica_with_the_job_source()
    {
        var (client, _, tracker) = Setup(replicas: 3);
        const string job = "daily-report-slimfaas-job-123";
        await client.PublishEventAsync("ws-function", new CustomRequest([], [], "ws-function", "/", "POST", ""), "report", default, job);
        var events = tracker.GetRecent();
        Assert.Equal(3, events.Count); Assert.Equal(3, events.Select(e => e.TargetPod).Distinct().Count());
        Assert.All(events, e => { Assert.Equal("event_publish", e.Type); Assert.Equal(job, e.SourcePod); Assert.Equal("ws-function", e.Target); });
    }
}
