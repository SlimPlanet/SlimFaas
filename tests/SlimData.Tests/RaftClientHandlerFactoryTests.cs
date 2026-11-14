using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SlimData.Options;

namespace SlimData.Tests;

[CollectionDefinition("Raft socket transport", DisableParallelization = true)]
public sealed class RaftSocketTransportCollection;

[Collection("Raft socket transport")]
public sealed class RaftClientHandlerFactoryTests
{
    [Fact]
    public async Task Successful_requests_reuse_the_connection_with_a_dns_endpoint()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Loopback, 0);
        if (Socket.OSSupportsIPv6)
            listener.Server.DualMode = true;
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var factory = new RaftClientHandlerFactory(
            Microsoft.Extensions.Options.Options.Create(new RaftClientHandlerOptions()),
            NullLogger<RaftClientHandlerFactory>.Instance);
        using var client = new HttpClient(factory.CreateHandler("raftClient"));
        Task server = ServeAsync();
        for (var request = 0; request < 2; request++)
        {
            string response = await client.GetStringAsync($"http://localhost:{endpoint.Port}/raft", deadline.Token);
            Assert.Equal("OK", response);
        }
        await server;

        async Task ServeAsync()
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync(deadline.Token);
            await using NetworkStream stream = peer.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            for (var request = 0; request < 2; request++)
            {
                string? line;
                do
                {
                    line = await reader.ReadLineAsync(deadline.Token);
                    Assert.NotNull(line);
                } while (line.Length != 0);
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nOK");
                await stream.WriteAsync(response, deadline.Token);
            }
        }
    }

    [Fact]
    public async Task Cancelled_upload_releases_the_owned_socket_without_waiting_for_the_peer()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        // Keep the receive buffer small before the handshake. Cancellation
        // must not depend on the peer consuming the upload.
        listener.Server.ReceiveBufferSize = 4096;
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var factory = new RaftClientHandlerFactory(
            Microsoft.Extensions.Options.Options.Create(new RaftClientHandlerOptions()),
            NullLogger<RaftClientHandlerFactory>.Instance);
        var handler = Assert.IsType<SocketsHttpHandler>(factory.CreateHandler("raftClient"));
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? connect = handler.ConnectCallback;
        var connectedSocket = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (connect is not null)
        {
            handler.ConnectCallback = async (context, token) =>
            {
                Stream stream = await connect(context, token);
                connectedSocket.SetResult(Assert.IsAssignableFrom<NetworkStream>(stream).Socket);
                return stream;
            };
        }
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}/raft")
        {
            Content = new ByteArrayContent(new byte[16 * 1024 * 1024])
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<HttpResponseMessage> sending = client.SendAsync(request, cancellation.Token);
        using Socket peer = await listener.AcceptSocketAsync(deadline.Token);
        Socket? sendingSocket = connect is null ? null : await connectedSocket.Task.WaitAsync(deadline.Token);
        while (peer.Available < 1024)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sending);
        client.Dispose();
        // Cancellation can complete before asynchronous transport teardown.
        // Observe owned-socket closure before allowing the peer to read.
        while (sendingSocket is not null && !sendingSocket.SafeHandle.IsClosed)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        var buffer = new byte[65536];
        try
        {
            while (await peer.ReceiveAsync(buffer, SocketFlags.None, deadline.Token) != 0)
                deadline.Token.ThrowIfCancellationRequested();
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
        {
            // EOF and reset both close the transport. The Linux cgroup control
            // separately checks that kernel socket memory does not accumulate.
        }
    }
}
