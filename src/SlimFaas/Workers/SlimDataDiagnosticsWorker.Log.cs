// LogTool: [LoggerMessage] definitions for SlimDataDiagnosticsWorker.cs (#358, phase 3).
namespace SlimFaas.Workers
{
    internal static partial class SlimDataDiagnosticsWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to record SlimData diagnostics")]
        internal static partial void LogUnableToRecordSlimDataDiagnostics(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning,
            Message = "SlimData Raft progress stalled for at least {ThresholdSeconds} seconds. Leader={Leader}, Term={Term}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}, AppliedLogIndex={AppliedLogIndex}, QueuedBatches={QueuedBatches}, Snapshotting={Snapshotting}, Restoring={Restoring}. Preserve the first Raft exception and its inner exceptions before restarting.")]
        internal static partial void LogRaftProgressStalled(this global::Microsoft.Extensions.Logging.ILogger logger, int thresholdSeconds,
            global::System.Net.EndPoint? leader, long term, long lastLogIndex, long committedLogIndex, long? appliedLogIndex,
            int queuedBatches, bool snapshotting, bool restoring);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "SlimData Raft progress stall indication cleared. Leader={Leader}, Term={Term}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}, AppliedLogIndex={AppliedLogIndex}")]
        internal static partial void LogRaftProgressStallCleared(this global::Microsoft.Extensions.Logging.ILogger logger,
            global::System.Net.EndPoint? leader, long term, long lastLogIndex, long committedLogIndex, long? appliedLogIndex);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning,
            Message = "SlimData Raft consensus unavailable. Leader={Leader}, HasConsensus={HasConsensus}, UnavailableSeconds={UnavailableSeconds}, Term={Term}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}, AppliedLogIndex={AppliedLogIndex}")]
        internal static partial void LogRaftConsensusUnavailable(this global::Microsoft.Extensions.Logging.ILogger logger,
            global::System.Net.EndPoint? leader, bool hasConsensus, double unavailableSeconds,
            long term, long lastLogIndex, long committedLogIndex, long? appliedLogIndex);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information,
            Message = "SlimData Raft consensus recovered. Leader={Leader}, Term={Term}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}, AppliedLogIndex={AppliedLogIndex}")]
        internal static partial void LogRaftConsensusRecovered(this global::Microsoft.Extensions.Logging.ILogger logger,
            global::System.Net.EndPoint? leader, long term, long lastLogIndex, long committedLogIndex, long? appliedLogIndex);

    }
}
