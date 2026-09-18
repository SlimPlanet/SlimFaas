// LogTool: [LoggerMessage] definitions for ScheduleJobBackupWorker.cs (#358, phase 3).
namespace SlimFaas.Workers
{
    internal static partial class ScheduleJobBackupWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "ScheduleJobBackupWorker: invalid SlimData:BackupIntervalSeconds={BackupIntervalSeconds}, using 1 second instead.")]
        internal static partial void LogScheduleJobBackupWorkerInvalidSlimDataBackupIntervalSecondsUsing1(this global::Microsoft.Extensions.Logging.ILogger logger, int backupIntervalSeconds);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: disabled (SlimData:BackupDirectory is not set)")]
        internal static partial void LogScheduleJobBackupWorkerDisabledSlimDataBackupDirectoryIsNot(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: starting — backupDir={BackupDir}, interval={Interval}s, coldStart={ColdStart}")]
        internal static partial void LogScheduleJobBackupWorkerStartingBackupDirIntervalColdStart(this global::Microsoft.Extensions.Logging.ILogger logger, string? backupDir, double interval, bool coldStart);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: entering periodic backup loop (interval={Interval}s)")]
        internal static partial void LogScheduleJobBackupWorkerEnteringPeriodicBackupLoopInterval(this global::Microsoft.Extensions.Logging.ILogger logger, double interval);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "ScheduleJobBackupWorker: unexpected error in backup loop")]
        internal static partial void LogScheduleJobBackupWorkerUnexpectedErrorInBackupLoop(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: stopped")]
        internal static partial void LogScheduleJobBackupWorkerStopped(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: not master — skipping restore")]
        internal static partial void LogScheduleJobBackupWorkerNotMasterSkippingRestore(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: no backup file found at {Path} — skipping restore")]
        internal static partial void LogScheduleJobBackupWorkerNoBackupFileFoundAt(this global::Microsoft.Extensions.Logging.ILogger logger, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: DB already has ScheduleJob data — skipping restore")]
        internal static partial void LogScheduleJobBackupWorkerDBAlreadyHasScheduleJobData(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: restoring from {Path}")]
        internal static partial void LogScheduleJobBackupWorkerRestoringFrom(this global::Microsoft.Extensions.Logging.ILogger logger, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "ScheduleJobBackupWorker: restore JSON metadata: length={Length}, sha256={Hash}")]
        internal static partial void LogScheduleJobBackupWorkerRestoreJSONMetadataLengthSha256(this global::Microsoft.Extensions.Logging.ILogger logger, int length, string hash);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: backup file is empty — nothing to restore")]
        internal static partial void LogScheduleJobBackupWorkerBackupFileIsEmptyNothing(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: restored {Count} schedule entries")]
        internal static partial void LogScheduleJobBackupWorkerRestoredScheduleEntries(this global::Microsoft.Extensions.Logging.ILogger logger, int count);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "ScheduleJobBackupWorker: error during restore")]
        internal static partial void LogScheduleJobBackupWorkerErrorDuringRestore(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "ScheduleJobBackupWorker: no change detected (hash={Hash}) — skipping write")]
        internal static partial void LogScheduleJobBackupWorkerNoChangeDetectedHashSkipping(this global::Microsoft.Extensions.Logging.ILogger logger, string hash);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "ScheduleJobBackupWorker: backup prepared — hash={Hash}, hashsetCount={Count}, jsonLength={Length}")]
        internal static partial void LogScheduleJobBackupWorkerBackupPreparedHashHashsetCountJsonLength(this global::Microsoft.Extensions.Logging.ILogger logger, string hash, int count, int length);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "ScheduleJobBackupWorker: backup written — {Count} hashset(s) to {Path}")]
        internal static partial void LogScheduleJobBackupWorkerBackupWrittenHashsetTo(this global::Microsoft.Extensions.Logging.ILogger logger, int count, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "ScheduleJobBackupWorker: error during backup")]
        internal static partial void LogScheduleJobBackupWorkerErrorDuringBackup(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
