using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Prometheus;
using SlimData;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.Streams;
using SlimFaas.WebSocket;
using MessageType = SlimFaas.WebSocket.WebSocketMessageType;

namespace SlimFaas.Tests.Workers;

public sealed class WebSocketPeerIntegrationTests
{
    [Fact]
    public async Task Single_connection_serves_sync_async_and_events_from_every_node()
    {
        await using var cluster = await Cluster.CreateAsync();
        await using var worker = await Worker.ConnectAsync(cluster.Nodes[1]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient();
        for (int index = 0; index < cluster.Nodes.Count; index++)
        {
            var node = cluster.Nodes[index];
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(node.Url, "/function/remote/echo?node=" + index))
            { Content = new StringContent("node-" + index) };
            using var response = await http.SendAsync(request, timeout.Token);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal("node-" + index, await response.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("owner", Assert.Single(response.Headers.GetValues("X-Worker")));
            var received = await worker.SyncRequests.Reader.ReadAsync(timeout.Token);
            Assert.Equal("DELETE", received.Method);
            Assert.Equal("echo", received.Path);
            Assert.Equal("?node=" + index, received.Query);

            string id = Guid.NewGuid().ToString("N");
            Assert.Equal(207, await node.Sender.SendAsync("remote", Request(), id, false, 1, timeout.Token));
            Assert.Equal(id, (await worker.AsyncRequests.Reader.ReadAsync(timeout.Token)).ElementId);
            using var published = await http.PostAsync(new Uri(node.Url, "/publish-event/signal"), new StringContent(id), timeout.Token);
            Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);
            Assert.Equal(id, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
                (await worker.Events.Reader.ReadAsync(timeout.Token)).Body!)));
        }
        Assert.Equal(1, cluster.Nodes.Sum(node => node.Registry.TotalConnections));
        Assert.Empty(cluster.Nodes[0].Registry.GetRegisteredFunctionNames());
        Assert.Empty(cluster.Nodes[2].Registry.GetRegisteredFunctionNames());
    }

    [Fact]
    public async Task Relay_is_duplex_before_upload_eof_and_transfers_32_mib_without_buffering_the_body()
    {
        await using var cluster = await Cluster.CreateAsync();
        await using var worker = await Worker.ConnectAsync(cluster.Nodes[1]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        await using var body = new GatedBody(32 * 1024 * 1024);
        var response = await cluster.Nodes[0].Sender.SendSyncRequestStreamAsync("remote", "PATCH", "echo", "?duplex=1",
            new() { ["Content-Type"] = ["application/octet-stream"], ["Connection"] = ["X-Hop"], ["X-Hop"] = ["hidden"] },
            body, ct: timeout.Token);
        Assert.Equal(206, response.StatusCode);
        Assert.False(body.Completed);
        byte[] first = await response.BodyChunks.ReadAsync(timeout.Token);
        Assert.Equal(32 * 1024, first.Length);
        Assert.False(body.Completed);
        var start = await worker.SyncRequests.Reader.ReadAsync(timeout.Token);
        Assert.False(start.Headers.ContainsKey("Connection"));
        Assert.False(start.Headers.ContainsKey("X-Hop"));
        Assert.False(response.Headers.ContainsKey("X-Hop"));
        Assert.Equal(2, response.Headers["Set-Cookie"].Length);
        long length = first.Length;
        Assert.DoesNotContain(first, value => value != 90);
        body.Release();
        await foreach (byte[] chunk in response.BodyChunks.ReadAllAsync(timeout.Token))
        {
            Assert.DoesNotContain(chunk, value => value != 90);
            length += chunk.Length;
        }
        await response.WaitForEnd();
        Assert.Equal(body.Length, length);
        Assert.True(body.Completed);
        Assert.Empty(Assert.Single(cluster.Nodes[1].Registry.GetConnections("remote")).PendingSyncStreams);
    }

    [Fact]
    public async Task Canceling_relay_cancels_owner_work_and_releases_admission()
    {
        await using var cluster = await Cluster.CreateAsync();
        await using var worker = await Worker.ConnectAsync(cluster.Nodes[1]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var body = new GatedBody(1024 * 1024);
        var response = await cluster.Nodes[0].Sender.SendSyncRequestStreamAsync("remote", "POST", "echo", "", [],
            body, ct: cancellation.Token);
        _ = await response.BodyChunks.ReadAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(response.WaitForEnd);
        await worker.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(Assert.Single(cluster.Nodes[1].Registry.GetConnections("remote")).PendingSyncStreams);
    }

    [Fact]
    public async Task Event_fanout_delivers_once_to_each_owner_without_recursive_public_calls()
    {
        await using var cluster = await Cluster.CreateAsync();
        await using var first = await Worker.ConnectAsync(cluster.Nodes[1]);
        await using var second = await Worker.ConnectAsync(cluster.Nodes[2]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await cluster.Nodes[0].Sender.PublishEventAsync("remote", Request(), "signal", ct: timeout.Token);
        Assert.Equal("signal", (await first.Events.Reader.ReadAsync(timeout.Token)).EventName);
        Assert.Equal("signal", (await second.Events.Reader.ReadAsync(timeout.Token)).EventName);
        Assert.False(first.Events.Reader.TryRead(out _));
        Assert.False(second.Events.Reader.TryRead(out _));
        Assert.Equal(2, Assert.Single(cluster.Nodes[0].Functions.GetVirtualDeployments()).Replicas);
    }

    [Fact]
    public async Task Remote_metadata_preserves_private_visibility_and_rejects_unsigned_internal_requests()
    {
        await using var cluster = await Cluster.CreateAsync();
        await using var worker = await Worker.ConnectAsync(cluster.Nodes[1]);
        using var http = new HttpClient();
        using var privateResponse = await http.GetAsync(new Uri(cluster.Nodes[0].Url, "/function/remote/private"));
        Assert.Equal(HttpStatusCode.NotFound, privateResponse.StatusCode);
        foreach (string operation in new[] { "async", "event" })
        {
            using var forged = await http.PostAsync(new Uri(cluster.Nodes[1].Url, "/internal/websocket/" + operation),
                new StringContent("{}"));
            Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        }
        Assert.False(worker.SyncRequests.Reader.TryRead(out _));
        Assert.False(worker.AsyncRequests.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Ambiguous_async_attempt_keeps_its_id_fenced_until_late_callback()
    {
        await using var cluster = await Cluster.CreateAsync();
        await using var worker = await Worker.ConnectAsync(cluster.Nodes[1]);
        worker.SuppressCallbacks = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string id = Guid.NewGuid().ToString("N");
        Task<int> attempt = cluster.Nodes[0].Sender.SendAsync("remote", Request(), id, false, 1, cancellation.Token);
        _ = await worker.AsyncRequests.Reader.ReadAsync(cancellation.Token);
        var connection = Assert.Single(cluster.Nodes[1].Registry.GetConnections("remote"));
        Task<int> ownerAttempt = connection.PendingCallbacks[id].Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ownerAttempt.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(503, await cluster.Nodes[0].Sender.SendAsync("remote", Request(), id, false, 2));
        await worker.CallbackAsync(id);
        worker.SuppressCallbacks = false;
        // A separate ID proves that owner capacity remains usable after an ambiguous attempt.
        Assert.Equal(207, await cluster.Nodes[0].Sender.SendAsync("remote", Request(), Guid.NewGuid().ToString("N"), true, 3));
    }

    private static CustomRequest Request() => new([], "payload"u8.ToArray(), "remote", "echo", "POST", "");

    private sealed record Node(WebApplication App, Uri Url, WebSocketConnectionRegistry Registry,
        IWebSocketSendClient Sender, IWebSocketFunctionRepository Functions);

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly List<WebSocketDirectoryConnection> _connections = [];
        private readonly List<PodInformation> _pods = [];
        public List<Node> Nodes { get; } = [];

        public static async Task<Cluster> CreateAsync()
        {
            var cluster = new Cluster();
            try
            {
                for (int index = 0; index < 3; index++)
                    await cluster.AddNodeAsync("node-" + index);
                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        private async Task AddNodeAsync(string name)
        {
            var owner = new WebSocketDirectoryOwner
            {
                Node = name, Incarnation = Guid.NewGuid().ToString("N"),
                RelaySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ExpiresAtTicks = DateTime.UtcNow.AddMinutes(2).Ticks
            };
            var directory = new Mock<IWebSocketClusterDirectory>();
            directory.SetupGet(value => value.Node).Returns(name);
            directory.SetupGet(value => value.Incarnation).Returns(owner.Incarnation);
            directory.Setup(value => value.Owner(name)).Returns(owner);
            directory.SetupGet(value => value.Connections).Returns(() =>
            {
                lock (_gate) return _connections.Where(connection => connection.Ready).ToArray();
            });
            directory.Setup(value => value.RefreshAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            directory.Setup(value => value.ReserveAsync(It.IsAny<WebSocketClientConnection>(), It.IsAny<CancellationToken>()))
                .Returns<WebSocketClientConnection, CancellationToken>((connection, _) =>
                {
                    lock (_gate)
                    {
                        string fingerprint = StreamConfiguration.Fingerprint(connection.Configuration);
                        if (_connections.Any(existing => existing.Function == connection.FunctionName && existing.Fingerprint != fingerprint))
                            return Task.FromResult(new WebSocketDirectoryResult { Status = 409, Error = "Configuration conflict." });
                        _connections.Add(new()
                        {
                            Id = connection.ConnectionId, Node = name, Incarnation = owner.Incarnation,
                            Function = connection.FunctionName, Fingerprint = fingerprint,
                            Configuration = JsonSerializer.Serialize(connection.Configuration, AppJsonContext.Default.WebSocketFunctionConfiguration)
                        });
                        return Task.FromResult(new WebSocketDirectoryResult { Status = 200 });
                    }
                });
            directory.Setup(value => value.ActivateAsync(It.IsAny<WebSocketClientConnection>(), It.IsAny<CancellationToken>()))
                .Returns<WebSocketClientConnection, CancellationToken>((connection, _) =>
                {
                    lock (_gate) _connections.Single(existing => existing.Id == connection.ConnectionId).Ready = true;
                    return Task.FromResult(new WebSocketDirectoryResult { Status = 200 });
                });
            directory.Setup(value => value.RemoveAsync(It.IsAny<WebSocketClientConnection>(), It.IsAny<CancellationToken>()))
                .Returns<WebSocketClientConnection, CancellationToken>((connection, _) =>
                {
                    lock (_gate) _connections.RemoveAll(existing => existing.Id == connection.ConnectionId);
                    return Task.CompletedTask;
                });
            var replicas = new Mock<IReplicasService>();
            replicas.SetupGet(value => value.Deployments).Returns(() => new DeploymentsInformations([],
                new SlimFaasDeploymentInformation(3, _pods.ToArray()), []));
            var jobs = new Mock<IJobService>();
            jobs.SetupGet(value => value.Jobs).Returns([]);
            var ns = new Mock<INamespaceProvider>();
            ns.SetupGet(value => value.CurrentNamespace).Returns("demo");
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Warning);
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(directory.Object);
            builder.Services.AddSingleton(replicas.Object);
            builder.Services.AddSingleton(jobs.Object);
            builder.Services.AddSingleton(ns.Object);
            int port = 0;
            var ports = new Mock<ISlimFaasPorts>();
            ports.SetupGet(value => value.Ports).Returns(() => [port]);
            builder.Services.AddSingleton(ports.Object);
            builder.Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
            {
                WebSocketPort = 0, BaseSlimDataUrl = "http://{pod_ip}:49999", BaseFunctionPodUrl = "http://{pod_ip}:{pod_port}"
            }));
            builder.Services.AddSingleton<WebSocketConnectionRegistry>();
            builder.Services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepository>();
            builder.Services.AddSingleton<WebSocketSendClient>();
            builder.Services.AddSingleton<IWebSocketSendClient, ClusterWebSocketSendClient>();
            builder.Services.AddSingleton<WebSocketPeerAuthorization>();
            builder.Services.AddSingleton<IFunctionAccessPolicy, DefaultFunctionAccessPolicy>();
            builder.Services.AddSingleton<NetworkActivityTracker>();
            builder.Services.AddSingleton<HistoryHttpMemoryService>();
            builder.Services.AddSingleton(Mock.Of<ISendClient>());
            builder.Services.AddHttpClient(ClusterWebSocketSendClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
            builder.Services.UseHttpClientMetrics(BoundedHttpClientMetrics.Create(Metrics.NewCustomRegistry()));
            var app = builder.Build();
            app.UseWebSockets();
            app.Use((context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/function"))
                    context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.1");
                return next(context);
            });
            app.MapWebSocketEndpoints();
            app.MapWebSocketPeerEndpoints();
            app.MapSyncFunctionEndpoints();
            app.MapEventEndpoints();
            await app.StartAsync();
            var url = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            port = url.Port;
            _pods.Add(new PodInformation(name, true, true, "127.0.0.1", "slimfaas", Ports: [49999, url.Port]));
            var node = new Node(app, url, app.Services.GetRequiredService<WebSocketConnectionRegistry>(),
                app.Services.GetRequiredService<IWebSocketSendClient>(), app.Services.GetRequiredService<IWebSocketFunctionRepository>());
            Nodes.Add(node);
            // Each directory resolves every owner's credential from the same authoritative snapshot.
            foreach (var existing in Nodes)
            {
                var existingDirectory = Mock.Get(existing.App.Services.GetRequiredService<IWebSocketClusterDirectory>());
                var existingOwner = existingDirectory.Object.Owner(existingDirectory.Object.Node);
                existingDirectory.Setup(value => value.Owner(name)).Returns(owner);
                directory.Setup(value => value.Owner(existingDirectory.Object.Node))
                    .Returns(existingOwner);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in Nodes)
            {
                await node.App.StopAsync();
                await node.App.DisposeAsync();
            }
        }
    }

    private sealed class Worker : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _loop = Task.CompletedTask;
        public Channel<SyncRequestStartPayload> SyncRequests { get; } = Channel.CreateUnbounded<SyncRequestStartPayload>();
        public Channel<AsyncRequestPayload> AsyncRequests { get; } = Channel.CreateUnbounded<AsyncRequestPayload>();
        public Channel<PublishEventPayload> Events { get; } = Channel.CreateUnbounded<PublishEventPayload>();
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool SuppressCallbacks;

        public static async Task<Worker> ConnectAsync(Node node)
        {
            var worker = new Worker();
            await worker._socket.ConnectAsync(new UriBuilder(node.Url) { Scheme = "ws", Path = "/ws" }.Uri, worker._stop.Token);
            await worker.SendAsync(new WebSocketEnvelope
            {
                Type = MessageType.Register, CorrelationId = Guid.NewGuid().ToString("N"),
                Payload = JsonSerializer.SerializeToElement(new RegisterPayload
                {
                    FunctionName = "remote", Configuration = new()
                    {
                        DefaultVisibility = "Public", NumberParallelRequestPerPod = 4,
                        SubscribeEvents = [new() { Name = "signal", Visibility = "Public" }],
                        PathsStartWithVisibility = [new() { Path = "private", Visibility = "Private" }]
                    }
                }, AppJsonContext.Default.RegisterPayload)
            });
            var (_, bytes) = await worker.ReceiveAsync();
            var response = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.WebSocketEnvelope)!;
            Assert.Equal(MessageType.RegisterResponse, response.Type);
            Assert.True(response.Payload!.Value.GetProperty("success").GetBoolean(), response.Payload.ToString());
            worker._loop = worker.RunAsync();
            return worker;
        }

        private async Task<(System.Net.WebSockets.WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync()
        {
            using var message = new MemoryStream();
            var buffer = new byte[64 * 1024];
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    throw new IOException("Worker socket closed.");
                await message.WriteAsync(buffer.AsMemory(0, result.Count), _stop.Token);
            } while (!result.EndOfMessage);
            return (result.MessageType, message.ToArray());
        }

        private Task SendAsync(WebSocketEnvelope envelope) => _socket.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(envelope, AppJsonContext.Default.WebSocketEnvelope).AsMemory(),
            System.Net.WebSockets.WebSocketMessageType.Text, true, _stop.Token).AsTask();

        public Task CallbackAsync(string id) => SendAsync(new()
        {
            Type = MessageType.AsyncCallback, CorrelationId = id,
            Payload = JsonSerializer.SerializeToElement(new AsyncCallbackPayload { ElementId = id, StatusCode = 207 },
                AppJsonContext.Default.AsyncCallbackPayload)
        });

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var (type, bytes) = await ReceiveAsync();
                    if (type == System.Net.WebSockets.WebSocketMessageType.Binary)
                    {
                        var header = BinaryFrame.DecodeHeader(bytes);
                        var payload = bytes.AsMemory(BinaryFrame.HeaderSize);
                        switch (header.Type)
                        {
                            case MessageType.SyncRequestStart:
                                await SyncRequests.Writer.WriteAsync(JsonSerializer.Deserialize(payload.Span,
                                    AppJsonContext.Default.SyncRequestStartPayload)!, _stop.Token);
                                await WebSocketPeerFrames.SendAsync(_socket, MessageType.SyncResponseStart, header.CorrelationId,
                                    JsonSerializer.SerializeToUtf8Bytes(new SyncResponseStartPayload
                                    {
                                        StatusCode = 206, Headers = new()
                                        {
                                            ["X-Worker"] = ["owner"], ["Content-Type"] = ["application/octet-stream"],
                                            ["Connection"] = ["X-Hop"], ["X-Hop"] = ["hidden"], ["Set-Cookie"] = ["a=1", "b=2"]
                                        }
                                    }, AppJsonContext.Default.SyncResponseStartPayload), _stop.Token);
                                break;
                            case MessageType.SyncRequestChunk:
                                await WebSocketPeerFrames.SendAsync(_socket, MessageType.SyncResponseChunk, header.CorrelationId, payload, _stop.Token);
                                break;
                            case MessageType.SyncRequestEnd:
                                await WebSocketPeerFrames.SendAsync(_socket, MessageType.SyncResponseEnd, header.CorrelationId, ReadOnlyMemory<byte>.Empty, _stop.Token);
                                break;
                            case MessageType.SyncCancel:
                                Canceled.TrySetResult();
                                break;
                        }
                    }
                    else
                    {
                        var envelope = JsonSerializer.Deserialize(bytes, AppJsonContext.Default.WebSocketEnvelope)!;
                        if (envelope.Type == MessageType.AsyncRequest)
                        {
                            var request = envelope.Payload!.Value.Deserialize(AppJsonContext.Default.AsyncRequestPayload)!;
                            await AsyncRequests.Writer.WriteAsync(request, _stop.Token);
                            if (!SuppressCallbacks) await CallbackAsync(request.ElementId);
                        }
                        else if (envelope.Type == MessageType.PublishEvent)
                            await Events.Writer.WriteAsync(envelope.Payload!.Value.Deserialize(AppJsonContext.Default.PublishEventPayload)!, _stop.Token);
                    }
                }
            }
            catch (Exception exception) when (_stop.IsCancellationRequested && exception is OperationCanceledException or WebSocketException or IOException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _socket.Abort();
            await _loop;
            _socket.Dispose();
            _stop.Dispose();
        }
    }

    private sealed class GatedBody(long length) : Stream
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _position;
        public bool Completed => _position == length;
        public void Release() => _gate.TrySetResult();
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= 32 * 1024) await _gate.Task.WaitAsync(cancellationToken);
            int count = (int)Math.Min(buffer.Length, length - _position);
            buffer.Span[..count].Fill(90);
            _position += count;
            return count;
        }
    }
}
