// LogTool: [LoggerMessage] definitions for WebSocketQueuesWorker.cs (#358, phase 3).
namespace SlimFaas.WebSocket
{
    internal static partial class WebSocketQueuesWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in WebSocketQueuesWorker")]
        internal static partial void LogErrorInWebSocketQueuesWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Failed to deserialize CustomRequest for WebSocket function {FunctionName}")]
        internal static partial void LogFailedToDeserializeCustomRequestForWebSocket(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "WebSocket async request failed for {FunctionName}/{ElementId}")]
        internal static partial void LogWebSocketAsyncRequestFailedFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "WebSocket async completed for {FunctionName} elementId={ElementId} statusCode={StatusCode}")]
        internal static partial void LogWebSocketAsyncCompletedForElementIdStatusCode(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string elementId, int statusCode);

    }
}
