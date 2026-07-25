namespace SlimFaas;

internal static class InternalHttpClientHandler
{
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
