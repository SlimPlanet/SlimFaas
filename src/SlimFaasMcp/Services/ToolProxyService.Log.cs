// LogTool: [LoggerMessage] definitions for ToolProxyService.cs (#358, phase 3).
namespace SlimFaasMcp.Services
{
    internal static partial class ToolProxyServiceLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "SlimFaasMcp → API {Method} {Url}\nHeaders: {Headers}\nBody: {Body}")]
        internal static partial void LogSlimFaasMcpAPIHeadersBody(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.Http.HttpMethod method, string url, string headers, string body);

    }
}
