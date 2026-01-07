using System.Text;

namespace SlimFaasMcp.Middleware;

public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    // Limite de taille de body loggé (en caractères)
    private const int MaxBodyLengthToLog = 4096;
    private const int MaxHeaderValueLength = 256;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Si le logger n'est pas en Debug, on ne fait rien (perf)
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            await _next(context);
            return;
        }

        var request = context.Request;

        if (request.Path.Value != "/mcp")
        {
            await _next(context);
            return;
        }

        // Headers (tronqués pour éviter de log trop gros)
        var headers = string.Join(", ",
            request.Headers.Select(h =>
                $"{h.Key}={Truncate(h.Value.ToString(), MaxHeaderValueLength)}"));

        // Body : on ne lit que les JSON et on tronque
        string bodyPreview = string.Empty;

        if (request.ContentLength is > 0 &&
            request.Body.CanRead &&
            request.ContentType is not null &&
            request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            request.EnableBuffering();

            using var reader = new StreamReader(
                request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024,
                leaveOpen: true);

            var buffer = new char[MaxBodyLengthToLog];
            int read = await reader.ReadBlockAsync(buffer, 0, buffer.Length);

            if (read > 0)
            {
                bodyPreview = new string(buffer, 0, read);

                if (request.ContentLength > MaxBodyLengthToLog)
                {
                    bodyPreview += "… (truncated)";
                }
            }

            // On remet le stream au début pour le reste du pipeline
            request.Body.Position = 0;
        }

        _logger.LogDebug(
            "Incoming HTTP {Method} {Path}{Query} Headers: {Headers} Body: {Body}",
            request.Method,
            request.Path.Value,
            request.QueryString.HasValue ? request.QueryString.Value : string.Empty,
            headers,
            bodyPreview);

        await _next(context);
    }

    private static string Truncate(string value, int maxLen)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLen)
            return value;

        return value[..maxLen] + "… (truncated)";
    }
}
