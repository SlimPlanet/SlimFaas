// LogTool: [LoggerMessage] definitions for MetricsWorker.cs (#358, phase 3).
namespace SlimFaas.Workers
{
    internal static partial class MetricsWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global Error in MetricsWorker")]
        internal static partial void LogGlobalErrorInMetricsWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
