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

            // Evite les connexions zombifiées derrière un LB
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),

            // HTTP/2: garde la connexion en vie (si HTTP/2 activé)
            KeepAlivePingDelay = TimeSpan.FromSeconds(15),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            EnableMultipleHttp2Connections = true,

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