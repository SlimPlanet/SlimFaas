// LogTool: [LoggerMessage] definitions for SlimFaasClient.cs (#358, phase 3).
namespace SlimFaasClient
{
    internal static partial class SlimFaasClientLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "SlimFaas registration failed (fatal): {Error}")]
        internal static partial void LogSlimFaasRegistrationFailedFatal(this global::Microsoft.Extensions.Logging.ILogger logger, string error);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "WebSocket disconnected ({Error}). Reconnecting in {Delay:F1} s…")]
        internal static partial void LogWebSocketDisconnectedReconnectingIn(this global::Microsoft.Extensions.Logging.ILogger logger, string error, double delay);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Connecting to SlimFaas WebSocket at {Uri} …")]
        internal static partial void LogConnectingToSlimFaasWebSocketAt(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri uri);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Connected. Registering function '{FunctionName}' …")]
        internal static partial void LogConnectedRegisteringFunction(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Registered successfully. connectionId={ConnectionId}")]
        internal static partial void LogRegisteredSuccessfullyConnectionId(this global::Microsoft.Extensions.Logging.ILogger logger, string connectionId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to deserialize WebSocket message")]
        internal static partial void LogFailedToDeserializeWebSocketMessage(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Received binary frame too short ({Length} bytes)")]
        internal static partial void LogReceivedBinaryFrameTooShortBytes(this global::Microsoft.Extensions.Logging.ILogger logger, int length);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to deserialize SyncRequestStart")]
        internal static partial void LogFailedToDeserializeSyncRequestStart(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unexpected binary frame type: {Type}")]
        internal static partial void LogUnexpectedBinaryFrameType(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimFaasClient.SlimFaasMessageType type);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Pong received")]
        internal static partial void LogPongReceived(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unhandled message type: {Type}")]
        internal static partial void LogUnhandledMessageType(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimFaasClient.SlimFaasMessageType type);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Received AsyncRequest for {ElementId} but no handler registered. Returning 500.")]
        internal static partial void LogReceivedAsyncRequestForButNoHandler(this global::Microsoft.Extensions.Logging.ILogger logger, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "AsyncRequest handler threw an exception for {ElementId}")]
        internal static partial void LogAsyncRequestHandlerThrewAnExceptionFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Received PublishEvent '{EventName}' but no handler registered.")]
        internal static partial void LogReceivedPublishEventButNoHandlerRegistered(this global::Microsoft.Extensions.Logging.ILogger logger, string eventName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "PublishEvent handler threw an exception for event '{EventName}'")]
        internal static partial void LogPublishEventHandlerThrewAnExceptionFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string eventName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Received SyncRequest for {CorrelationId} but no handler registered. Returning 500.")]
        internal static partial void LogReceivedSyncRequestForButNoHandler(this global::Microsoft.Extensions.Logging.ILogger logger, string correlationId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "SyncRequest handler threw an exception for {CorrelationId}")]
        internal static partial void LogSyncRequestHandlerThrewAnExceptionFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string correlationId);

    }
}
