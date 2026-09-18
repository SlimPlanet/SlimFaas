// LogTool: [LoggerMessage] definitions for StatusEndpoints.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class StatusEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "WakeAll failed for {FunctionName}")]
        internal static partial void LogWakeAllFailedFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.AggregateException exception, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Wake failed for {FunctionName}")]
        internal static partial void LogWakeFailedFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.AggregateException exception, string functionName);

    }
}
