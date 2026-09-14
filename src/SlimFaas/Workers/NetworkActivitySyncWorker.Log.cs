// LogTool: [LoggerMessage] definitions for NetworkActivitySyncWorker.cs (#358, phase 3).
namespace SlimFaas.Workers
{
    internal static partial class NetworkActivitySyncWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Could not synchronize peer activity")]
        internal static partial void LogCouldNotSynchronizePeerActivity(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Could not read activity from {Peer}")]
        internal static partial void LogCouldNotReadActivityFrom(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string peer);

    }
}
