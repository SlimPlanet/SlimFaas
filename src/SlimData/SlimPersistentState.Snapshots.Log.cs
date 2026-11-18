namespace SlimData;

internal static partial class SlimPersistentStateSnapshotsLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Retained truncated SlimData snapshot {SnapshotFile} at index {Index}; trying an older complete snapshot.")]
    internal static partial void LogTruncatedSnapshotRetained(
        this ILogger logger, Exception exception, string snapshotFile, long index);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Snapshot creation failed before shutdown; its temporary copy was not published and the retained WAL remains authoritative.")]
    internal static partial void LogSnapshotCreationFailedDuringShutdown(this ILogger logger, Exception exception);
}
