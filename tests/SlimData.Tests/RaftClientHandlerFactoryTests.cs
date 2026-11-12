using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SlimData.Options;

namespace SlimData.Tests;

public sealed class RaftClientHandlerFactoryTests
{
    [Fact]
    public async Task Successful_requests_reuse_the_connection_with_a_dns_endpoint()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
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
    public async Task Cancelled_upload_resets_a_nonreading_peer_instead_of_retaining_queued_bytes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var factory = new RaftClientHandlerFactory(
            Microsoft.Extensions.Options.Options.Create(new RaftClientHandlerOptions()),
            NullLogger<RaftClientHandlerFactory>.Instance);
        using var client = new HttpClient(factory.CreateHandler("raftClient"));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{endpoint.Port}/raft")
        {
            Content = new ByteArrayContent(new byte[16 * 1024 * 1024])
        };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<HttpResponseMessage> sending = client.SendAsync(request, cancellation.Token);
        using Socket peer = await listener.AcceptSocketAsync(deadline.Token);
        while (peer.Available < 65536)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await sending);
        client.Dispose(); // Synchronize ownership teardown before observing the TCP peer.

        bool reset = false;
        var buffer = new byte[65536];
        try
        {
            while (await peer.ReceiveAsync(buffer, SocketFlags.None, deadline.Token) != 0)
            {
            }
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionReset)
        {
            reset = true;
        }

        Assert.True(reset, "A cancelled large request must release its queued TCP payload immediately.");
    }
}
