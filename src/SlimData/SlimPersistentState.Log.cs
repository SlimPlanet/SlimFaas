// LogTool: [LoggerMessage] definitions for SlimPersistentState.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class SlimPersistentStateLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Recovered zero-prefixed SlimData Raft log entry. Index={Index}, Term={Term}, CommandId={CommandId}, OriginalLength={OriginalLength}, DiscardedPrefixBytes={DiscardedPrefixBytes}")]
        internal static partial void LogRecoveredZeroPrefixedSlimDataRaftLog(this global::Microsoft.Extensions.Logging.ILogger logger, long index, long term, int? commandId, long? originalLength, int discardedPrefixBytes);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Skipping incompatible SlimData Raft log entry. Index={Index}, Term={Term}, CommandId={CommandId}, Length={Length}, Violation={Violation}, LeadingZeroBytes={LeadingZeroBytes}, CurrentEnvelopeOffset={CurrentEnvelopeOffset}, Reason={Reason}")]
        internal static partial void LogSkippingIncompatibleSlimDataRaftLogEntry(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception? exception, long index, long term, int? commandId, long? length, global::SlimData.Commands.SlimDataCommandViolation violation, long leadingZeroBytes, int currentEnvelopeOffset, string reason);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Persisting SlimData snapshot. KeyValues={KeyValues}, Hashsets={Hashsets}, Queues={Queues}, PayloadBytes={PayloadBytes}, ManagedMemoryBytes={ManagedMemoryBytes}")]
        internal static partial void LogPersistingSlimDataSnapshotKeyValuesHashsetsQueues(this global::Microsoft.Extensions.Logging.ILogger logger, int keyValues, int hashsets, int queues, long payloadBytes, long managedMemoryBytes);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData snapshot persisted. DurationMilliseconds={DurationMilliseconds}, PayloadBytes={PayloadBytes}, ManagedMemoryBytes={ManagedMemoryBytes}")]
        internal static partial void LogSlimDataSnapshotPersistedDurationMillisecondsPayloadBytesManagedMemoryBytes(this global::Microsoft.Extensions.Logging.ILogger logger, long durationMilliseconds, long payloadBytes, long managedMemoryBytes);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Restoring SlimData snapshot. File={SnapshotFile}, FileBytes={FileBytes}, ManagedMemoryBytes={ManagedMemoryBytes}")]
        internal static partial void LogRestoringSlimDataSnapshotFileFileBytesManagedMemoryBytes(this global::Microsoft.Extensions.Logging.ILogger logger, string snapshotFile, long fileBytes, long managedMemoryBytes);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData snapshot restore completed. DurationMilliseconds={DurationMilliseconds}, ManagedMemoryBytes={ManagedMemoryBytes}")]
        internal static partial void LogSlimDataSnapshotRestoreCompletedDurationMillisecondsManagedMemoryBytes(this global::Microsoft.Extensions.Logging.ILogger logger, long durationMilliseconds, long managedMemoryBytes);

    }
}
