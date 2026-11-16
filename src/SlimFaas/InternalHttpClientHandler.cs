namespace SlimFaas;

internal static class InternalHttpClientHandler
{
    public static SocketsHttpHandler CreateForFunctions(bool allowUnsecureSsl)
        => Create(allowUnsecureSsl, TimeSpan.FromMinutes(5));

    public static SocketsHttpHandler CreateForSlimData(bool allowUnsecureSsl)
        => Create(allowUnsecureSsl, TimeSpan.FromMinutes(2));

    public static SocketsHttpHandler CreateForClusterPeers(bool allowUnsecureSsl)
        => Create(allowUnsecureSsl, TimeSpan.FromMinutes(2));

    private static SocketsHttpHandler Create(bool allowUnsecureSsl, TimeSpan connectionLifetime)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseProxy = false,
            PooledConnectionLifetime = connectionLifetime
        };

        if (allowUnsecureSsl)
        {
#pragma warning disable CA5359 // Preserve the existing opt-in for self-signed internal TLS.
            handler.SslOptions.RemoteCertificateValidationCallback = static (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return handler;
    }

}
