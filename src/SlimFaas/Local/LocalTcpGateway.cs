using System.Net;
using System.Net.Sockets;

namespace SlimFaas.Local;

public sealed class LocalTcpGateway : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly LocalNodeManager _nodes;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _acceptTask;
    private int _next;

    public LocalTcpGateway(int port, LocalNodeManager nodes)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _nodes = nodes;
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = AcceptLoopAsync(_stopping.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            IReadOnlyList<int> ready = _nodes.ReadyHttpPorts;
            if (ready.Count == 0)
            {
                const string body = "SlimFaas is not ready.\n";
                byte[] unavailable = System.Text.Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\n" +
                    $"Content-Length: {System.Text.Encoding.ASCII.GetByteCount(body)}\r\n\r\n{body}");
                await client.GetStream().WriteAsync(unavailable, cancellationToken);
                return;
            }

            int index = (int)((uint)(Interlocked.Increment(ref _next) - 1) % (uint)ready.Count);
            using var upstream = new TcpClient();
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, ready[index], cancellationToken);
                NetworkStream clientStream = client.GetStream();
                NetworkStream upstreamStream = upstream.GetStream();
                Task toUpstream = clientStream.CopyToAsync(upstreamStream, cancellationToken);
                Task toClient = upstreamStream.CopyToAsync(clientStream, cancellationToken);
                await Task.WhenAny(toUpstream, toClient);
            }
            catch (Exception exception) when (
                exception is SocketException or IOException or OperationCanceledException)
            {
                // This is deliberately not retried. A new connection will select
                // another ready node.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        if (_acceptTask is not null)
            await _acceptTask;
        _stopping.Dispose();
    }
}
