// LogTool: [LoggerMessage] definitions for WebSocketSendClient.cs (#358, phase 3).
namespace SlimFaas.WebSocket
{
    internal static partial class WebSocketSendClientLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "No WebSocket client available for function {FunctionName}")]
        internal static partial void LogNoWebSocketClientAvailableForFunction(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "AsyncRequest sent via WebSocket to {FunctionName}/{ConnectionId} elementId={ElementId}")]
        internal static partial void LogAsyncRequestSentViaWebSocketToElementId(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string connectionId, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "WebSocket async request timed out for {FunctionName}/{ElementId}")]
        internal static partial void LogWebSocketAsyncRequestTimedOutFor(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error sending WebSocket async request to {FunctionName}/{ElementId}")]
        internal static partial void LogErrorSendingWebSocketAsyncRequestTo(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "No WebSocket client available for sync stream to {FunctionName}")]
        internal static partial void LogNoWebSocketClientAvailableForSync(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "SyncRequest stream sent to {FunctionName}/{ConnectionId} correlationId={CorrelationId}")]
        internal static partial void LogSyncRequestStreamSentToCorrelationId(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string connectionId, string correlationId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to send WebSocket message to {ConnectionId}")]
        internal static partial void LogFailedToSendWebSocketMessageTo(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string connectionId);

    }
}
