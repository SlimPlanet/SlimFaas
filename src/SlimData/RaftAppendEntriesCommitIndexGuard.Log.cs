// LogTool: [LoggerMessage] definitions for RaftAppendEntriesCommitIndexGuard.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class RaftAppendEntriesCommitIndexGuardLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Bounded Raft AppendEntries commit index to the last entry carried by the request. RequestedCommitIndex={RequestedCommitIndex}, ClampedCommitIndex={ClampedCommitIndex}, PrecedingRecordIndex={PrecedingRecordIndex}, EntriesCount={EntriesCount}, TotalClampedRequests={TotalClampedRequests}")]
        internal static partial void LogBoundedRaftAppendEntriesCommitIndexTo(this global::Microsoft.Extensions.Logging.ILogger logger, long requestedCommitIndex, long clampedCommitIndex, long precedingRecordIndex, long entriesCount, long totalClampedRequests);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Bounded Raft AppendEntries commit index. RequestedCommitIndex={RequestedCommitIndex}, ClampedCommitIndex={ClampedCommitIndex}, TotalClampedRequests={TotalClampedRequests}")]
        internal static partial void LogBoundedRaftAppendEntriesCommitIndexRequestedCommitIndex(this global::Microsoft.Extensions.Logging.ILogger logger, long requestedCommitIndex, long clampedCommitIndex, long totalClampedRequests);

    }
}
