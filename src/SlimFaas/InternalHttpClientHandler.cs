namespace SlimFaas;

internal static class InternalHttpClientHandler
{
    public static SocketsHttpHandler CreateForFunctions(bool allowUnsecureSsl)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseProxy = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

        if (allowUnsecureSsl)
        {
#pragma warning disable CA5359 // Preserve the existing opt-in for self-signed internal TLS.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return handler;
    }

    public static HttpClientHandler Create(bool allowUnsecureSsl)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            // SlimData and function pods are internal orchestrator endpoints. Consulting
            // an OS/PAC proxy per request is incorrect and leaks CFNetwork callback
            // resources on macOS.
            UseProxy = false
        };

        if (allowUnsecureSsl)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        return handler;
    }
}
