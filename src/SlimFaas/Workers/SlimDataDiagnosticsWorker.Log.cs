// LogTool: [LoggerMessage] definitions for SlimDataDiagnosticsWorker.cs (#358, phase 3).
namespace SlimFaas.Workers
{
    internal static partial class SlimDataDiagnosticsWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to record SlimData diagnostics")]
        internal static partial void LogUnableToRecordSlimDataDiagnostics(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning,
            Message = "SlimData Raft progress stalled for at least 30 seconds. Leader={Leader}, Term={Term}, LastLogIndex={LastLogIndex}, CommittedIndex={CommittedIndex}, AppliedIndex={AppliedIndex}, QueuedBatches={QueuedBatches}, Snapshotting={Snapshotting}, Restoring={Restoring}. Preserve the first Raft exception and its inner exceptions before restarting.")]
        internal static partial void LogRaftProgressStalled(this global::Microsoft.Extensions.Logging.ILogger logger,
            global::System.Net.EndPoint? leader, long term, long lastLogIndex, long committedIndex, long appliedIndex,
            int queuedBatches, bool snapshotting, bool restoring);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "SlimData Raft progress stall indication cleared. Leader={Leader}, Term={Term}, LastLogIndex={LastLogIndex}, CommittedIndex={CommittedIndex}, AppliedIndex={AppliedIndex}")]
        internal static partial void LogRaftProgressStallCleared(this global::Microsoft.Extensions.Logging.ILogger logger,
            global::System.Net.EndPoint? leader, long term, long lastLogIndex, long committedIndex, long? appliedIndex);

    }
}
