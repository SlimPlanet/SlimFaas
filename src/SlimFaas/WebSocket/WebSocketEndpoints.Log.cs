// LogTool: [LoggerMessage] definitions for WebSocketEndpoints.cs (#358, phase 3).
namespace SlimFaas.WebSocket
{
    internal static partial class WebSocketEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to deserialize WebSocket message")]
        internal static partial void LogFailedToDeserializeWebSocketMessage(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Received binary frame too short ({Length} bytes)")]
        internal static partial void LogReceivedBinaryFrameTooShortBytes(this global::Microsoft.Extensions.Logging.ILogger logger, int length);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Received binary frame for unknown correlationId={CorrelationId} type={Type}")]
        internal static partial void LogReceivedBinaryFrameForUnknownCorrelationId(this global::Microsoft.Extensions.Logging.ILogger logger, string correlationId, global::SlimFaas.WebSocket.WebSocketMessageType type);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "SyncResponseStart received: correlationId={CorrelationId} status={StatusCode}")]
        internal static partial void LogSyncResponseStartReceivedCorrelationIdStatus(this global::Microsoft.Extensions.Logging.ILogger logger, string correlationId, int statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to deserialize SyncResponseStart payload")]
        internal static partial void LogFailedToDeserializeSyncResponseStartPayload(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "SyncResponseEnd received: correlationId={CorrelationId}")]
        internal static partial void LogSyncResponseEndReceivedCorrelationId(this global::Microsoft.Extensions.Logging.ILogger logger, string correlationId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "SyncCancel received: correlationId={CorrelationId}")]
        internal static partial void LogSyncCancelReceivedCorrelationId(this global::Microsoft.Extensions.Logging.ILogger logger, string correlationId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unexpected binary frame type: {Type}")]
        internal static partial void LogUnexpectedBinaryFrameType(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimFaas.WebSocket.WebSocketMessageType type);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unhandled WebSocket message type: {Type}")]
        internal static partial void LogUnhandledWebSocketMessageType(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimFaas.WebSocket.WebSocketMessageType type);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to deserialize Register payload")]
        internal static partial void LogFailedToDeserializeRegisterPayload(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "WebSocket registration refused for '{FunctionName}': {Error}")]
        internal static partial void LogWebSocketRegistrationRefusedFor(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string? error);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to deserialize AsyncCallback payload")]
        internal static partial void LogFailedToDeserializeAsyncCallbackPayload(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "AsyncCallback received with missing elementId")]
        internal static partial void LogAsyncCallbackReceivedWithMissingElementId(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "AsyncCallback resolved: elementId={ElementId} status={Status}")]
        internal static partial void LogAsyncCallbackResolvedElementIdStatus(this global::Microsoft.Extensions.Logging.ILogger logger, string elementId, int status);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "AsyncCallback for unknown elementId={ElementId}")]
        internal static partial void LogAsyncCallbackForUnknownElementId(this global::Microsoft.Extensions.Logging.ILogger logger, string elementId);

    }
}
