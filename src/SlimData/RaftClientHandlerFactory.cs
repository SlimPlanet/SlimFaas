using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimData.Options;

namespace SlimData;

internal sealed class RaftClientHandlerFactory : IHttpMessageHandlerFactory
{
    private readonly RaftClientHandlerOptions _options;
    private readonly ILogger<RaftClientHandlerFactory> _logger;

    public RaftClientHandlerFactory(IOptions<RaftClientHandlerOptions> options, ILogger<RaftClientHandlerFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public HttpMessageHandler CreateHandler(string name)
    {
        _logger.LogRaftClientHandlerFactoryCreateHandlerWithConnectTimeoutMs(name, _options.ConnectTimeoutMilliseconds);
        
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(_options.ConnectTimeoutMilliseconds),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(_options.PooledConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(_options.PooledConnectionIdleTimeoutSeconds),
            MaxConnectionsPerServer = _options.MaxConnectionsPerServer,
            EnableMultipleHttp2Connections = false,
            UseProxy = false,
            ConnectCallback = ConnectAsync
        };
#pragma warning disable CA5359 // Raft members talk over self-signed intra-cluster TLS; hardening tracked in #346
        handler.SslOptions.RemoteCertificateValidationCallback = AllowCertificate;
#pragma warning restore CA5359
        return handler;
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            // A cancelled large RPC must discard its unsent payload. A graceful
            // close can retain megabytes per retry until a silent peer returns.
            socket.LingerState = new LingerOption(enable: true, seconds: 0);
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null; // The returned stream owns the connected socket.
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    internal static bool AllowCertificate(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        return true;
    }
}