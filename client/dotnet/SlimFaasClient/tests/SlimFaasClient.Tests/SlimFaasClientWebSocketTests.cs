using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
        Assert.Equal(WebSocketMessageType.Text, type);
        return JsonSerializer.Deserialize(data, SlimFaasClientJsonContext.Default.SlimFaasEnvelope)!;
    }

    public async Task<(SlimFaasMessageType Type, string CorrelationId, byte Flags, byte[] Payload)> NextFrameAsync()
    {
        var (type, data) = await NextAsync();
        Assert.Equal(WebSocketMessageType.Binary, type);
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

        Assert.Equal(SlimFaasMessageType.Register, connection.Register.Type);
        var payload = connection.Register.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.RegisterPayloadDto)!;
        Assert.Equal("ws-job", payload.FunctionName);
        Assert.Equal(["other"], payload.Configuration.DependsOn);
        Assert.Equal("Private", payload.Configuration.SubscribeEvents.Single().Visibility);
        Assert.Equal("/admin", payload.Configuration.PathsStartWithVisibility.Single().Path);
        Assert.Equal("Untrusted", payload.Configuration.DefaultTrust);
        Assert.Equal("conn-1", client.ConnectionId);

        await running.DisposeAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task RunForeverAsync_ThrowsWhenTheServerRefusesTheRegistration()
    {
        await using var server = await FakeSlimFaasServer.StartAsync();
        server.RegistrationSucceeds = false;
        server.RegistrationError = "name already taken";
        await using var client = new SlimFaasClient(server.Uri, TestHelpers.MakeConfig(), FastOptions());

        var act = () => client.RunForeverAsync(CancellationToken.None).WaitAsync(s_timeout);

        var exception = await Assert.ThrowsAnyAsync<SlimFaasRegistrationException>(act);
        Assert.Equal("name already taken", exception.Message);
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
        Assert.Equal(SlimFaasMessageType.AsyncCallback, callback.Type);
        Assert.Equal("el-1", callback.CorrelationId);
        var dto = callback.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!;
        Assert.Equal("el-1", dto.ElementId);
        Assert.Equal(204, dto.StatusCode);
        Assert.NotNull(received);
        Assert.Equal("PUT", received!.Method);
        Assert.Equal("/compute", received.Path);
        Assert.Equal("?n=3", received.Query);
        Assert.Equal(["1"], received.Headers["x-test"]);
        Assert.Equal("payload", Encoding.UTF8.GetString(received.Body!));
        Assert.True(received.IsLastTry);
        Assert.Equal(2, received.TryNumber);
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
        Assert.Equal(500, first.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!.StatusCode);
        Assert.Equal("no-handler", first.CorrelationId);

        client.OnAsyncRequest = _ => throw new InvalidOperationException("boom");
        await connection.SendTextAsync(TestHelpers.MakeEnvelope(SlimFaasMessageType.AsyncRequest, new { elementId = "throws" }));
        var second = await connection.NextEnvelopeAsync();
        Assert.Equal(500, second.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!.StatusCode);
        Assert.Equal("throws", second.CorrelationId);
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
        Assert.Equal(SlimFaasMessageType.AsyncCallback, callback.Type);
        Assert.Equal(201, callback.Payload!.Value.Deserialize(SlimFaasClientJsonContext.Default.AsyncCallbackDto)!.StatusCode);
    }

    [Fact]
    public void RegistrationException_HasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        Assert.NotNull(new SlimFaasRegistrationException().Message);
        Assert.Equal("refused", new SlimFaasRegistrationException("refused").Message);
        Assert.Same(inner, new SlimFaasRegistrationException("refused", inner).InnerException);
    }

    [Fact]
    public async Task SendCallbackAsync_ThrowsWhenNotConnected()
    {
        await using var client = new SlimFaasClient(new Uri("ws://127.0.0.1:1/ws"), TestHelpers.MakeConfig());

        var callback = () => client.SendCallbackAsync("e", 200);
        var start = () => client.SendSyncResponseStartAsync("c", new SlimFaasSyncResponse());
        var chunk = () => client.SendSyncResponseChunkAsync("c", new byte[1]);
        var end = () => client.SendSyncResponseEndAsync("c");

        await Assert.ThrowsAnyAsync<InvalidOperationException>(callback);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(start);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(chunk);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(end);
        await client.SendSyncCancelAsync("c");
        Assert.False(client.IsConnected);
        Assert.Null(client.ConnectionId);
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
        Assert.Equal("bad", first.EventName);

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
        Assert.Equal("order-created", second.EventName);
        Assert.Equal("/orders", second.Path);
        Assert.Equal("?a=1", second.Query);
        Assert.Equal(["v"], second.Headers["h"]);
        Assert.Equal("order", Encoding.UTF8.GetString(second.Body!));
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
        Assert.Equal(SlimFaasMessageType.SyncResponseStart, responseStart.Type);
        Assert.Equal(correlationId, responseStart.CorrelationId);
        var startDto = JsonSerializer.Deserialize(responseStart.Payload, SlimFaasClientJsonContext.Default.SyncResponseStartDto)!;
        Assert.Equal(201, startDto.StatusCode);
        Assert.Equal(["text/plain"], startDto.Headers["Content-Type"]);

        var chunk1 = await connection.NextFrameAsync();
        Assert.Equal(SlimFaasMessageType.SyncResponseChunk, chunk1.Type);
        Assert.Equal("hello ", Encoding.UTF8.GetString(chunk1.Payload));
        var chunk2 = await connection.NextFrameAsync();
        Assert.Equal("world", Encoding.UTF8.GetString(chunk2.Payload));

        var responseEnd = await connection.NextFrameAsync();
        Assert.Equal(SlimFaasMessageType.SyncResponseEnd, responseEnd.Type);
        Assert.Equal(BinaryFrame.FlagEndOfStream, responseEnd.Flags);

        Assert.Equal("abcd", receivedBody);
        Assert.Equal("POST", receivedRequest!.Method);
        Assert.Equal("/sync", receivedRequest.Path);
        Assert.Equal("?q=1", receivedRequest.Query);
        Assert.Equal(["y"], receivedRequest.Headers["x"]);
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
        Assert.Equal(SlimFaasMessageType.SyncResponseStart, responseStart.Type);
        Assert.Equal(500, JsonSerializer.Deserialize(responseStart.Payload, SlimFaasClientJsonContext.Default.SyncResponseStartDto)!.StatusCode);
        Assert.Equal(SlimFaasMessageType.SyncResponseEnd, (await connection.NextFrameAsync()).Type);
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
        Assert.Equal(SlimFaasMessageType.SyncResponseStart, responseStart.Type);
        Assert.Equal(500, JsonSerializer.Deserialize(responseStart.Payload, SlimFaasClientJsonContext.Default.SyncResponseStartDto)!.StatusCode);
        Assert.Equal(SlimFaasMessageType.SyncResponseEnd, (await connection.NextFrameAsync()).Type);
        Assert.IsType<OperationCanceledException>(handlerError);

        await client.SendSyncCancelAsync(correlationId);
        Assert.Equal(SlimFaasMessageType.SyncCancel, (await connection.NextFrameAsync()).Type);
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
        Assert.Equal("still-alive", evt.EventName);
        Assert.True(client.IsConnected);
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
        Assert.Equal(SlimFaasMessageType.Ping, ping.Type);
        Assert.False(string.IsNullOrEmpty(ping.CorrelationId));
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
        Assert.Equal(SlimFaasMessageType.Register, third.Register.Type);
    }

    [Fact]
    public async Task RunForeverAsync_RetriesWhenTheServerIsUnreachable()
    {
        await using var client = new SlimFaasClient(new Uri("ws://127.0.0.1:1/ws"), TestHelpers.MakeConfig(), FastOptions());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var run = client.RunForeverAsync(cts.Token);

        await run.WaitAsync(s_timeout);
        Assert.False(client.IsConnected);
    }
}
