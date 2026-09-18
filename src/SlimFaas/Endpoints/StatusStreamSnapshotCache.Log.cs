// LogTool: [LoggerMessage] definitions for StatusStreamSnapshotCache.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class StatusStreamSnapshotCacheLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to read queue length for function {FunctionName}.")]
        internal static partial void LogUnableToReadQueueLengthFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to build jobs snapshot for status stream.")]
        internal static partial void LogUnableToBuildJobsSnapshotFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
