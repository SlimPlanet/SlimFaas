// LogTool: [LoggerMessage] definitions for SendClient.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class SendClientLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Start sending sync request to {FunctionName}{FunctionPath}{FunctionQuery}")]
        internal static partial void LogStartSendingSyncRequestTo(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string functionPath, string functionQuery);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Sending async request to {TargetUrl}")]
        internal static partial void LogSendingAsyncRequestTo(this global::Microsoft.Extensions.Logging.ILogger logger, string targetUrl);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in SendHttpRequestAsync to {FunctionName} to {FunctionPath} ")]
        internal static partial void LogErrorInSendHttpRequestAsyncToTo(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName, string functionPath);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Start sending sync request to {FunctionName}{FunctionPath}{FunctionQuery}")]
        internal static partial void LogStartSendingSyncRequestTo2(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, string functionPath, string functionQuery);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Sending sync request to {TargetUrl}")]
        internal static partial void LogSendingSyncRequestTo(this global::Microsoft.Extensions.Logging.ILogger logger, string targetUrl);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in SendHttpRequestSync to {FunctionName} to {FunctionPath} ")]
        internal static partial void LogErrorInSendHttpRequestSyncToTo(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName, string functionPath);

    }
}
