// LogTool: [LoggerMessage] definitions for RequestLoggingMiddleware.cs (#358, phase 3).
namespace SlimFaasMcp.Middleware
{
    internal static partial class RequestLoggingMiddlewareLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Incoming HTTP {Method} {Path}{Query} Headers: {Headers} Body: {Body}")]
        internal static partial void LogIncomingHTTPHeadersBody(this global::Microsoft.Extensions.Logging.ILogger logger, string method, string path, string query, string headers, string body);

    }
}
