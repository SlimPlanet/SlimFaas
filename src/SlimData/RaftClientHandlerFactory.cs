using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace SlimData;

internal sealed class RaftClientHandlerFactory : IHttpMessageHandlerFactory
{
    public HttpMessageHandler CreateHandler(string name)
    {
        var slimDataSocketsHttpHandlerTimeoutDefault =
            Environment.GetEnvironmentVariable(EnvironmentVariables.SlimDataSocketsHttpHandlerTimeout) ??
            EnvironmentVariables.SlimDataSocketsHttpHandlerTimeoutDefault;
        if (!int.TryParse(slimDataSocketsHttpHandlerTimeoutDefault, out int electionTimeout))
        {
            throw new Exception("SLIMDATA_SOCKETS_HTTP_HANDLER_TIMEOUT is not an integer");
        }
        Console.WriteLine($"RaftClientHandlerFactory.CreateHandler({name}) with electionTimeout {electionTimeout}");
        var handler = new SocketsHttpHandler
        {
            // Etablissement TCP+TLS : vise 2s en prod K8s/mesh
            ConnectTimeout = TimeSpan.FromMilliseconds(electionTimeout),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 100,
            EnableMultipleHttp2Connections = false,

            UseProxy = false
            
        };
        handler.SslOptions.RemoteCertificateValidationCallback = AllowCertificate;
        handler.UseProxy = false;
        return handler;
    }

    internal static bool AllowCertificate(object sender, X509Certificate? certificate, X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        return true;
    }
}