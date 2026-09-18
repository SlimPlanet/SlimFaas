// LogTool: [LoggerMessage] definitions for SyncFunctionEndpoints.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class SyncFunctionEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "{FunctionName} not found 404")]
        internal static partial void LogNotFound404(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "{FunctionName} not found 404 because is private 404")]
        internal static partial void LogNotFound404BecauseIsPrivate(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Request aborted by client for {FunctionName}")]
        internal static partial void LogRequestAbortedByClientFor(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Unhandled sync error for {FunctionName}")]
        internal static partial void LogUnhandledSyncErrorFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "No WebSocket client available for {FunctionName}")]
        internal static partial void LogNoWebSocketClientAvailableFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.InvalidOperationException exception, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Request aborted by client for {FunctionName}")]
        internal static partial void LogRequestAbortedByClientFor2(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "WaitForAnyPodStartedAsync: {FunctionName} is ready (EndpointReady={EndpointReady}).")]
        internal static partial void LogWaitForAnyPodStartedAsyncIsReadyEndpointReady(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, bool endpointReady);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Pod {PodName} Ready={Ready} IP={Ip}")]
        internal static partial void LogPodReadyIP(this global::Microsoft.Extensions.Logging.ILogger logger, string podName, bool? ready, string ip);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "WaitForAnyPodStartedAsync: timeout ({Timeout}s) atteint pour {FunctionName}.")]
        internal static partial void LogWaitForAnyPodStartedAsyncTimeoutAtteintPour(this global::Microsoft.Extensions.Logging.ILogger logger, double timeout, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "WaitForAnyPodStartedAsync: annulé pour {FunctionName}.")]
        internal static partial void LogWaitForAnyPodStartedAsyncAnnulPour(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

    }
}
