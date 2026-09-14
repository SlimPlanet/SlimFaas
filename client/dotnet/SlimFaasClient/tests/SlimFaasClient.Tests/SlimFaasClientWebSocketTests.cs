using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SlimFaasClient.Tests;

// ---------------------------------------------------------------------------
// In-process WebSocket server that speaks the SlimFaas protocol
// ---------------------------------------------------------------------------

internal sealed class ServerConnection
{
    private readonly WebSocket _socket;

    public ServerConnection(WebSocket socket, SlimFaasEnvelope register)
    {
        _socket = socket;
        Register = register;
    }

    public SlimFaasEnvelope Register { get; }

    public Channel<(WebSocketMessageType Type, byte[] Data)> Received { get; } =
        Channel.CreateUnbounded<(WebSocketMessageType, byte[])>();

    public async Task<(WebSocketMessageType Type, byte[] Data)> NextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await Received.Reader.ReadAsync(cts.Token);
    }

    public async Task<SlimFaasEnvelope> NextEnvelopeAsync()
    {
        var (type, data) = await NextAsync();
        type.Should().Be(WebSocketMessageType.Text);
        return JsonSerializer.Deserialize(data, SlimFaasClientJsonContext.Default.SlimFaasEnvelope)!;
    }

    public async Task<(SlimFaasMessageType Type, string CorrelationId, byte Flags, byte[] Payload)> NextFrameAsync()
    {
        var (type, data) = await NextAsync();
        type.Should().Be(WebSocketMessageType.Binary);
        var (frameType, correlationId, flags, length) = BinaryFrame.DecodeHeader(data);
        return (frameType, correlationId, flags, data.AsSpan(BinaryFrame.HeaderSize, length).ToArray());
    }

    public Task SendTextAsync(string json) =>
        _socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, CancellationToken.None);

    public Task SendBinaryAsync(byte[] frame) =>
        _socket.SendAsync(new ArraySegment<byte>(frame), WebSocketMessageType.Binary, true, CancellationToken.None);

    public Task CloseAsync() =>
        _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

    public void Abort() => _socket.Abort();
}

internal sealed class FakeSlimFaasServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _connectionCount;

    private FakeSlimFaasServer(WebApplication app)
    {
        _app = app;
    }

    public Uri Uri { get; private set; } = null!;

    public bool RegistrationSucceeds { get; set; } = true;

    public string RegistrationError { get; set; } = "registration refused";

    public Channel<ServerConnection> Connections { get; } = Channel.CreateUnbounded<ServerConnection>();

    public static async Task<FakeSlimFaasServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var server = new FakeSlimFaasServer(app);

        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await server.HandleAsync(socket, context.RequestAborted);
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Uri = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");
        return server;
    }

    public async Task<ServerConnection> NextConnectionAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await Connections.Reader.ReadAsync(cts.Token);
    }

    private async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        var first = await ReadMessageAsync(socket, ct);
        if (first is null) return;

        var register = JsonSerializer.Deserialize(first.Value.Data, SlimFaasClientJsonContext.Default.SlimFaasEnvelope)!;
        var connection = new ServerConnection(socket, register);
        var number = Interlocked.Increment(ref _connectionCount);

        var response = RegistrationSucceeds
            ? TestHelpers.MakeEnvelope(SlimFaasMessageType.RegisterResponse, new { success = true, connectionId = $"conn-{number}" }, register.CorrelationId)
            : TestHelpers.MakeEnvelope(SlimFaasMessageType.RegisterResponse, new { success = false, error = RegistrationError }, register.CorrelationId);
        await connection.SendTextAsync(response);
        Connections.Writer.TryWrite(connection);

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var message = await ReadMessageAsync(socket, ct);
                if (message is null)
                {
                    try
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        // the client may already be gone
                    }
                    break;
                }

                connection.Received.Writer.TryWrite(message.Value);
            }
        }
        catch (WebSocketException)
        {
            // aborted by one side or the other
        }
        catch (OperationCanceledException)
        {
            // server shutting down
        }
        finally
        {
            connection.Received.Writer.TryComplete();
        }
    }

    private static async Task<(WebSocketMessageType Type, byte[] Data)?> ReadMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return (result.MessageType, ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public sealed class SlimFaasClientWebSocketTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    private static SlimFaasClientOptions FastOptions(double pingInterval = 0) => new()
    {
        ReconnectDelay = 0.05,
        PingInterval = pingInterval,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Owns a client and its RunForeverAsync loop; disposal cancels the loop, waits for it and
    /// disposes the client, so a failed assertion never leaves a reconnect loop running.
    /// </summary>
    private sealed class RunningClient : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;
        private bool _disposed;

        public RunningClient(Uri uri, SlimFaasClientConfig config, SlimFaasClientOptions options)
        {
            Client = new SlimFaasClient(uri, config, options);
        }

        public SlimFaasClient Client { get; }

        public void Start() => _run = Client.RunForeverAsync(_cts.Token);

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                await _cts.CancelAsync();
                await _run.WaitAsync(s_timeout);
            }
            finally
            {
                await Client.DisposeAsync();
                _cts.Dispose();
            }
        }
    }

    [Fact]
    public async Task RunForeverAsync_RegistersWithTheServerAndExposesTheConnectionId()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        var config = new SlimFaasClientConfig
        {
            FunctionName = "ws-job",
            DependsOn = ["other"],
            SubscribeEvents = [new SubscribeEventConfig { Name = "ev", Visibility = FunctionVisibility.Private }],
            PathsStartWithVisibility = [new PathVisibilityConfig { Path = "/admin", Visibility = FunctionVisibility.Private }],
            DefaultTrust = FunctionTrust.Untrusted,
        };
        await using var running = new RunningClient(server.Uri, config, FastOptions());
        var client = running.Client;

        running.Start();
        var connection = await server.NextConnectionAsync();
        await WaitUntilAsync(() => client.IsConnected);

        connection.Register.Type.Should().Be(SlimFaasMessageType.Register);
        var payload = connection.Register.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.RegisterPayloadDto)!;
        payload.FunctionName.Should().Be("ws-job");
        payload.Configuration.DependsOn.Should().Equal("other");
        payload.Configuration.SubscribeEvents.Single().Visibility.Should().Be("Private");
        payload.Configuration.PathsStartWithVisibility.Single().Path.Should().Be("/admin");
        payload.Configuration.DefaultTrust.Should().Be("Untrusted");
        client.ConnectionId.Should().Be("conn-1");

        await running.DisposeAsync();
        client.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task RunForeverAsync_ThrowsWhenTheServerRefusesTheRegistration()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        server.RegistrationSucceeds = false;
        server.RegistrationError = "name already taken";
        await using var client = new SlimFaasClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());

        var act = () => client.RunForeverAsync(CancellationToken.None).WaitAsync(s_timeout);

        await act.Should().ThrowAsync<SlimFaasRegistrationException>().WithMessage("name already taken");
    }

    [Fact]
    public async Task AsyncRequest_InvokesTheHandlerAndSendsTheCallback()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        SlimFaasAsyncRequest? received = null;
        client.OnAsyncRequest = req =>
        {
            received = req;
            return Task.FromResult(204);
        };
        running.Start();
        var connection = await server.NextConnectionAsync();

        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.AsyncRequest, new
        {
            elementId = "el-1",
            method = "PUT",
            path = "/compute",
            query = "?n=3",
            headers = new Dictionary<string, string[]> { ["x-test"] = ["1"] },
            body = Convert.ToBase64String("payload"u8.ToArray()),
            isLastTry = true,
            tryNumber = 2,
        }));

        var callback = await connection.NextEnvelopeAsync();
        callback.Type.Should().Be(SlimFaasMessageType.AsyncCallback);
        callback.CorrelationId.Should().Be("el-1");
        var dto = callback.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!;
        dto.ElementId.Should().Be("el-1");
        dto.StatusCode.Should().Be(204);
        received.Should().NotBeNull();
        received!.Method.Should().Be("PUT");
        received.Path.Should().Be("/compute");
        received.Query.Should().Be("?n=3");
        received.Headers["x-test"].Should().Equal("1");
        Encoding.UTF8.GetString(received.Body!).Should().Be("payload");
        received.IsLastTry.Should().BeTrue();
        received.TryNumber.Should().Be(2);
    }

    [Fact]
    public async Task AsyncRequest_Returns500WhenThereIsNoHandlerOrTheHandlerThrows()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        running.Start();
        var connection = await server.NextConnectionAsync();

        // without handlers an event is dropped and a request is answered with 500
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.PublishEvent, new { eventName = "dropped" }));
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.AsyncRequest, new { elementId = "no-handler" }));
        var first = await connection.NextEnvelopeAsync();
        first.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!.StatusCode.Should().Be(500);
        first.CorrelationId.Should().Be("no-handler");

        client.OnAsyncRequest = _ => throw new InvalidOperationException("boom");
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.AsyncRequest, new { elementId = "throws" }));
        var second = await connection.NextEnvelopeAsync();
        second.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!.StatusCode.Should().Be(500);
        second.CorrelationId.Should().Be("throws");
    }

    [Fact]
    public async Task AsyncRequest_Accepted_DefersTheCallbackToSendCallbackAsync()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        var handled = new TaskCompletionSource();
        client.OnAsyncRequest = _ =>
        {
            handled.TrySetResult();
            return Task.FromResult(202);
        };
        running.Start();
        var connection = await server.NextConnectionAsync();

        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.AsyncRequest, new { elementId = "long" }));
        await handled.Task.WaitAsync(s_timeout);
        await client.SendCallbackAsync("long", 201);

        var callback = await connection.NextEnvelopeAsync();
        callback.Type.Should().Be(SlimFaasMessageType.AsyncCallback);
        callback.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!.StatusCode.Should().Be(201);
    }

    [Fact]
    public void RegistrationException_HasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        new SlimFaasRegistrationException().Message.Should().NotBeNull();
        new SlimFaasRegistrationException("refused").Message.Should().Be("refused");
        new SlimFaasRegistrationException("refused", inner).InnerException.Should().BeSameAs(inner);
    }

    [Fact]
    public async Task SendCallbackAsync_ThrowsWhenNotConnected()
    {
        await using var client = new SlimFaasClient(new Uri("ws://127.0.0.1:1/ws"), TestHelpers.MakeConfig());

        var callback = () => client.SendCallbackAsync("e", 200);
        var start = () => client.SendSyncResponseStartAsync("c", new SlimFaasSyncResponse());
        var chunk = () => client.SendSyncResponseChunkAsync("c", new byte[1]);
        var end = () => client.SendSyncResponseEndAsync("c");

        await callback.Should().ThrowAsync<InvalidOperationException>();
        await start.Should().ThrowAsync<InvalidOperationException>();
        await chunk.Should().ThrowAsync<InvalidOperationException>();
        await end.Should().ThrowAsync<InvalidOperationException>();
        await client.SendSyncCancelAsync("c");
        client.IsConnected.Should().BeFalse();
        client.ConnectionId.Should().BeNull();
    }

    [Fact]
    public async Task PublishEvent_InvokesTheHandlerAndSurvivesHandlerErrors()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        var events = Channel.CreateUnbounded<SlimFaasPublishEvent>();
        client.OnPublishEvent = evt =>
        {
            events.Writer.TryWrite(evt);
            return evt.EventName == "bad" ? throw new InvalidOperationException("bad event") : Task.CompletedTask;
        };
        running.Start();
        var connection = await server.NextConnectionAsync();

        // Handlers run on separate tasks, so their order is not guaranteed: wait for the
        // first handler (which throws) to be observed before sending the second event.
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.PublishEvent, new { eventName = "bad" }));
        var first = await events.Reader.ReadAsync(new CancellationTokenSource(s_timeout).Token);
        first.EventName.Should().Be("bad");

        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.PublishEvent, new
        {
            eventName = "order-created",
            method = "POST",
            path = "/orders",
            query = "?a=1",
            headers = new Dictionary<string, string[]> { ["h"] = ["v"] },
            body = Convert.ToBase64String("order"u8.ToArray()),
        }));
        var second = await events.Reader.ReadAsync(new CancellationTokenSource(s_timeout).Token);
        second.EventName.Should().Be("order-created");
        second.Path.Should().Be("/orders");
        second.Query.Should().Be("?a=1");
        second.Headers["h"].Should().Equal("v");
        Encoding.UTF8.GetString(second.Body!).Should().Be("order");
    }

    [Fact]
    public async Task SyncRequest_StreamsTheBodyInAndTheResponseOut()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        string? receivedBody = null;
        SlimFaasSyncRequest? receivedRequest = null;
        client.OnSyncRequest = async req =>
        {
            receivedRequest = req;
            using var ms = new MemoryStream();
#pragma warning disable CA1835 // the array-based overloads are part of the public surface and exercised on purpose
            var scratch = new byte[3];
            var read = await req.Body.ReadAsync(scratch, 0, scratch.Length, CancellationToken.None);
            ms.Write(scratch, 0, read);
            await req.Body.CopyToAsync(ms);
            receivedBody = Encoding.UTF8.GetString(ms.ToArray());
            await req.Response.StartAsync(201, new Dictionary<string, string[]> { ["Content-Type"] = ["text/plain"] });
            await req.Response.WriteAsync("hello "u8.ToArray());
            await req.Response.WriteAsync("world"u8.ToArray(), 0, 5, CancellationToken.None);
#pragma warning restore CA1835
            await req.Response.CompleteAsync();
        };
        running.Start();
        var connection = await server.NextConnectionAsync();

        var correlationId = Guid.NewGuid().ToString();
        var start = new SyncRequestStartDto
        {
            Method = "POST",
            Path = "/sync",
            Query = "?q=1",
            Headers = new Dictionary<string, string[]> { ["x"] = ["y"] },
        };
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestStart, correlationId,
            JsonSerializer.SerializeToUtf8Bytes(start, SlimFaasClientJsonContext.Default.SyncRequestStartDto)));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestChunk, correlationId, "ab"u8));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestChunk, correlationId, "cd"u8));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestEnd, correlationId, BinaryFrame.FlagEndOfStream));

        var responseStart = await connection.NextFrameAsync();
        responseStart.Type.Should().Be(SlimFaasMessageType.SyncResponseStart);
        responseStart.CorrelationId.Should().Be(correlationId);
        var startDto = JsonSerializer.Deserialize(responseStart.Payload, SlimFaasClientJsonContext.Default.SyncResponseStartDto)!;
        startDto.StatusCode.Should().Be(201);
        startDto.Headers["Content-Type"].Should().Equal("text/plain");

        var chunk1 = await connection.NextFrameAsync();
        chunk1.Type.Should().Be(SlimFaasMessageType.SyncResponseChunk);
        Encoding.UTF8.GetString(chunk1.Payload).Should().Be("hello ");
        var chunk2 = await connection.NextFrameAsync();
        Encoding.UTF8.GetString(chunk2.Payload).Should().Be("world");

        var responseEnd = await connection.NextFrameAsync();
        responseEnd.Type.Should().Be(SlimFaasMessageType.SyncResponseEnd);
        responseEnd.Flags.Should().Be(BinaryFrame.FlagEndOfStream);

        receivedBody.Should().Be("abcd");
        receivedRequest!.Method.Should().Be("POST");
        receivedRequest.Path.Should().Be("/sync");
        receivedRequest.Query.Should().Be("?q=1");
        receivedRequest.Headers["x"].Should().Equal("y");
    }

    [Fact]
    public async Task SyncRequest_Returns500WhenThereIsNoHandler()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        running.Start();
        var connection = await server.NextConnectionAsync();

        var correlationId = Guid.NewGuid().ToString();
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestStart, correlationId,
            JsonSerializer.SerializeToUtf8Bytes(new SyncRequestStartDto(), SlimFaasClientJsonContext.Default.SyncRequestStartDto)));

        var responseStart = await connection.NextFrameAsync();
        responseStart.Type.Should().Be(SlimFaasMessageType.SyncResponseStart);
        JsonSerializer.Deserialize(responseStart.Payload, SlimFaasClientJsonContext.Default.SyncResponseStartDto)!.StatusCode.Should().Be(500);
        (await connection.NextFrameAsync()).Type.Should().Be(SlimFaasMessageType.SyncResponseEnd);
    }

    [Fact]
    public async Task SyncRequest_CancelledByTheServer_FailsTheHandlerWith500()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        Exception? handlerError = null;
        client.OnSyncRequest = async req =>
        {
            try
            {
                using var ms = new MemoryStream();
                await req.Body.CopyToAsync(ms);
            }
            catch (OperationCanceledException ex)
            {
                handlerError = ex;
                throw;
            }
        };
        running.Start();
        var connection = await server.NextConnectionAsync();

        var correlationId = Guid.NewGuid().ToString();
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestStart, correlationId,
            JsonSerializer.SerializeToUtf8Bytes(new SyncRequestStartDto(), SlimFaasClientJsonContext.Default.SyncRequestStartDto)));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncCancel, correlationId));

        var responseStart = await connection.NextFrameAsync();
        responseStart.Type.Should().Be(SlimFaasMessageType.SyncResponseStart);
        JsonSerializer.Deserialize(responseStart.Payload, SlimFaasClientJsonContext.Default.SyncResponseStartDto)!.StatusCode.Should().Be(500);
        (await connection.NextFrameAsync()).Type.Should().Be(SlimFaasMessageType.SyncResponseEnd);
        handlerError.Should().BeOfType<OperationCanceledException>();

        await client.SendSyncCancelAsync(correlationId);
        (await connection.NextFrameAsync()).Type.Should().Be(SlimFaasMessageType.SyncCancel);
    }

    [Fact]
    public async Task ReceiveLoop_IgnoresMalformedAndUnexpectedMessages()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        var events = Channel.CreateUnbounded<SlimFaasPublishEvent>();
        client.OnPublishEvent = evt =>
        {
            events.Writer.TryWrite(evt);
            return Task.CompletedTask;
        };
        running.Start();
        var connection = await server.NextConnectionAsync();

        await connection.SendTextAsync("{not json");
        await connection.SendTextAsync("null");
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.Pong, null));
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.RegisterResponse, null));
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.AsyncRequest, null));
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.PublishEvent, null));
        await connection.SendBinaryAsync([1, 2, 3]);
        await connection.SendBinaryAsync(BinaryFrame.Encode((SlimFaasMessageType)0x7F, Guid.NewGuid().ToString()));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestChunk, Guid.NewGuid().ToString(), "x"u8));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestEnd, Guid.NewGuid().ToString()));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncCancel, Guid.NewGuid().ToString()));
        await connection.SendBinaryAsync(BinaryFrame.Encode(SlimFaasMessageType.SyncRequestStart, Guid.NewGuid().ToString(), "{oops"u8));

        // the connection is still alive and processing
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.PublishEvent, new { eventName = "still-alive" }));
        var evt = await events.Reader.ReadAsync(new CancellationTokenSource(s_timeout).Token);
        evt.EventName.Should().Be("still-alive");
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task PingLoop_SendsPingsAtTheConfiguredInterval()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions(pingInterval: 0.02));
        var client = running.Client;
        running.Start();
        var connection = await server.NextConnectionAsync();

        var ping = await connection.NextEnvelopeAsync();
        ping.Type.Should().Be(SlimFaasMessageType.Ping);
        ping.CorrelationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RunForeverAsync_ReconnectsAfterTheServerClosesOrDropsTheConnection()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        await using var running = new RunningClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());
        var client = running.Client;
        running.Start();

        var first = await server.NextConnectionAsync();
        await WaitUntilAsync(() => client.ConnectionId == "conn-1");
        await first.CloseAsync();

        var second = await server.NextConnectionAsync();
        await WaitUntilAsync(() => client.ConnectionId == "conn-2");
        second.Abort();

        var third = await server.NextConnectionAsync();
        await WaitUntilAsync(() => client.ConnectionId == "conn-3");
        third.Register.Type.Should().Be(SlimFaasMessageType.Register);
    }

    [Fact]
    public async Task RunForeverAsync_RetriesWhenTheServerIsUnreachable()
    {
        await using var client = new SlimFaasClient(new Uri("ws://127.0.0.1:1/ws"), TestHelpers.MakeConfig(), FastOptions());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var run = client.RunForeverAsync(cts.Token);

        await run.WaitAsync(s_timeout);
        client.IsConnected.Should().BeFalse();
    }
}
