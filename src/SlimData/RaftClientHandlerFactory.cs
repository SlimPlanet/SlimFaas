using System.Net;
using System.Net.Security;
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
        _logger.LogInformation("RaftClientHandlerFactory.CreateHandler({Name}) with ConnectTimeout {ConnectTimeout}ms", 
            name, _options.ConnectTimeoutMilliseconds);
        
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(_options.ConnectTimeoutMilliseconds),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(_options.PooledConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(_options.PooledConnectionIdleTimeoutSeconds),
            MaxConnectionsPerServer = _options.MaxConnectionsPerServer,
            EnableMultipleHttp2Connections = false,
            UseProxy = false
        };
        handler.SslOptions.RemoteCertificateValidationCallback = AllowCertificate;
        return handler;
    }

    internal static bool AllowCertificate(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        return true;
    }
}