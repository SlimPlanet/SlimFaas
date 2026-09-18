// LogTool: [LoggerMessage] definitions for SlimDataExpirationCleanupWorker.cs (#358, phase 3).
namespace SlimData.Expiration
{
    internal static partial class SlimDataExpirationCleanupWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "SlimData TTL cleanup cycle started.")]
        internal static partial void LogSlimDataTTLCleanupCycleStarted(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData TTL cleanup cycle failed.")]
        internal static partial void LogSlimDataTTLCleanupCycleFailed(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
