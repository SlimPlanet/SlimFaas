// LogTool: [LoggerMessage] definitions for WebSocketConnectionRegistry.cs (#358, phase 3).
namespace SlimFaas.WebSocket
{
    internal static partial class WebSocketConnectionRegistryLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "WebSocket client registered: connectionId={ConnectionId}, functionName={FunctionName}")]
        internal static partial void LogWebSocketClientRegisteredConnectionIdFunctionName(this global::Microsoft.Extensions.Logging.ILogger logger, string connectionId, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "WebSocket client unregistered: connectionId={ConnectionId}, functionName={FunctionName}")]
        internal static partial void LogWebSocketClientUnregisteredConnectionIdFunctionName(this global::Microsoft.Extensions.Logging.ILogger logger, string connectionId, string functionName);

    }
}
