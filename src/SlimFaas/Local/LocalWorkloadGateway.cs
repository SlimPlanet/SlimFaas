using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SlimFaas.Local;

/// <summary>
/// Gives one native local workload its own loopback entrypoint. Native
/// processes share the host IP, so the proxy adds the workload identity to the
/// first HTTP request before forwarding it to the regular local entrypoint.
/// </summary>
internal sealed class LocalWorkloadGateway : IAsyncDisposable
{
    internal const string JobHeaderName = "X-SlimFaas-Job";
    internal const string SignatureHeaderName = "X-SlimFaas-Job-Signature";
    internal const string PodHeaderName = "X-SlimFaas-Pod";
    internal const string PodSignatureHeaderName = "X-SlimFaas-Pod-Signature";
    private const int MaximumHeaderBytes = 64 * 1024;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _targetPort;
    private readonly string _workloadName;
    private readonly string _signature;
    private readonly bool _functionPod;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private Task? _acceptTask;
    private long _connectionId;

    public LocalWorkloadGateway(int targetPort, string workloadName, string token, bool functionPod = false)
    {
        _targetPort = targetPort;
        _workloadName = workloadName;
        _functionPod = functionPod;
        _signature = functionPod ? CreatePodSignature(workloadName, token) : CreateSignature(workloadName, token);
    }

    public int Port { get; private set; }

    public void Start()
    {
        if (_acceptTask is not null)
            return;

        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(_stopping.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                long connectionId = Interlocked.Increment(ref _connectionId);
                Task connection = HandleAsync(client, cancellationToken);
                _connections.TryAdd(connectionId, connection);
                _ = connection.ContinueWith(
                    completed => _connections.TryRemove(connectionId, out Task? _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
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
        using (var upstream = new TcpClient())
        {
            try
            {
                await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, cancellationToken);
                NetworkStream clientStream = client.GetStream();
                NetworkStream upstreamStream = upstream.GetStream();
                byte[] requestStart = await ReadRequestStartAsync(clientStream, cancellationToken);
                if (requestStart.Length == 0)
                    return;

                byte[] attributedRequest = AddIdentity(
                    requestStart,
                    _workloadName,
                    _signature, _functionPod);
                await upstreamStream.WriteAsync(attributedRequest, cancellationToken);

                Task toUpstream = clientStream.CopyToAsync(upstreamStream, cancellationToken);
                Task toClient = upstreamStream.CopyToAsync(clientStream, cancellationToken);
                await Task.WhenAny(toUpstream, toClient);
            }
            catch (Exception exception) when (
                exception is SocketException
                    or IOException
                    or OperationCanceledException
                    or InvalidDataException)
            {
                // The caller observes the same closed connection it would get
                // from an unavailable local SlimFaas entrypoint.
            }
        }
    }

    private static async Task<byte[]> ReadRequestStartAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        using var request = new MemoryStream();
        byte[] buffer = new byte[4096];
        while (request.Length < MaximumHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return request.ToArray();

            request.Write(buffer, 0, read);
            if (FindHeaderEnd(request.GetBuffer(), checked((int)request.Length)) >= 0)
                return request.ToArray();
        }

        throw new InvalidDataException(
            $"The local workload request headers exceed {MaximumHeaderBytes} bytes.");
    }

    internal static byte[] AddIdentity(
        byte[] requestStart,
        string workloadName,
        string signature,
        bool functionPod = false)
    {
        int headerEnd = FindHeaderEnd(requestStart, requestStart.Length);
        if (headerEnd < 0 ||
            requestStart.AsSpan().StartsWith("PRI * HTTP/2.0"u8))
        {
            return requestStart;
        }

        string headerText = Encoding.Latin1.GetString(requestStart, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
        bool isUpgrade = lines.Skip(1).Any(line =>
            line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
        var output = new StringBuilder(headerText.Length + workloadName.Length + 64);
        output.Append(lines[0]).Append("\r\n");
        foreach (string line in lines.Skip(1))
        {
            if (line.StartsWith($"{JobHeaderName}:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith($"{SignatureHeaderName}:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith($"{PodHeaderName}:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith($"{PodSignatureHeaderName}:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!isUpgrade && line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                continue;
            output.Append(line).Append("\r\n");
        }

        output.Append(functionPod ? PodHeaderName : JobHeaderName).Append(": ").Append(workloadName).Append("\r\n");
        output.Append(functionPod ? PodSignatureHeaderName : SignatureHeaderName).Append(": ").Append(signature).Append("\r\n");
        if (!isUpgrade)
            output.Append("Connection: close\r\n");
        output.Append("\r\n");

        byte[] enrichedHeaders = Encoding.Latin1.GetBytes(output.ToString());
        int bodyOffset = headerEnd + 4;
        byte[] result = new byte[enrichedHeaders.Length + requestStart.Length - bodyOffset];
        enrichedHeaders.CopyTo(result, 0);
        requestStart.AsSpan(bodyOffset).CopyTo(result.AsSpan(enrichedHeaders.Length));
        return result;
    }

    internal static string CreateSignature(string workloadName, string token)
        => Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(token),
            Encoding.UTF8.GetBytes(workloadName)));

    internal static string CreatePodSignature(string podName, string token)
        => CreateSignature("pod:" + podName, token);

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var index = 0; index <= length - 4; index++)
        {
            if (buffer[index] == '\r' &&
                buffer[index + 1] == '\n' &&
                buffer[index + 2] == '\r' &&
                buffer[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Dispose();
        if (_acceptTask is not null)
            await _acceptTask;
        await Task.WhenAll(_connections.Values);
        _stopping.Dispose();
    }
}
