// LogTool: [LoggerMessage] definitions for SlimDataExpirationCleaner.cs (#358, phase 3).
namespace SlimData.Expiration
{
    internal static partial class SlimDataExpirationCleanerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deleting expired keyvalue. key={Key}")]
        internal static partial void LogDeletingExpiredKeyvalueKey(this global::Microsoft.Extensions.Logging.ILogger logger, string key);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete expired keyvalue. key={Key}")]
        internal static partial void LogFailedToDeleteExpiredKeyvalueKey(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string key);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deleting expired keyvalue. key={Key}")]
        internal static partial void LogDeletingExpiredKeyvalueKey2(this global::Microsoft.Extensions.Logging.ILogger logger, string key);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete expired hashset. key={Key}")]
        internal static partial void LogFailedToDeleteExpiredHashsetKey(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string key);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deleting expired local file by disk metadata. id={Id} expireAt={ExpireAt}")]
        internal static partial void LogDeletingExpiredLocalFileByDisk(this global::Microsoft.Extensions.Logging.ILogger logger, string id, long expireAt);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete expired local file. id={Id}")]
        internal static partial void LogFailedToDeleteExpiredLocalFile(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deleting confirmed local file without Raft metadata. id={Id}")]
        internal static partial void LogDeletingConfirmedLocalFileWithoutRaft(this global::Microsoft.Extensions.Logging.ILogger logger, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete local file without Raft metadata. id={Id}")]
        internal static partial void LogFailedToDeleteLocalFileWithout(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deleting confirmed orphaned offload file. id={Id} QueueElementId={QueueElementId}")]
        internal static partial void LogDeletingConfirmedOrphanedOffloadFileId(this global::Microsoft.Extensions.Logging.ILogger logger, string id, string? queueElementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete orphaned offload file. id={Id}")]
        internal static partial void LogFailedToDeleteOrphanedOffloadFile(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to read metadata. key={Key} Value={Value}")]
        internal static partial void LogFailedToReadMetadataKeyValue(this global::Microsoft.Extensions.Logging.ILogger logger, string key, global::System.ReadOnlyMemory<byte> @value);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deleting confirmed orphaned offload metadata. key={Key} QueueElementId={QueueElementId}")]
        internal static partial void LogDeletingConfirmedOrphanedOffloadMetadataKey(this global::Microsoft.Extensions.Logging.ILogger logger, string key, string queueElementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Cleaned up {Count} orphan .tmp file(s) from disk.")]
        internal static partial void LogCleanedUpOrphanTmpFileFrom(this global::Microsoft.Extensions.Logging.ILogger logger, int count);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "No orphan .tmp files found during cleanup.")]
        internal static partial void LogNoOrphanTmpFilesFoundDuring(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to cleanup orphan .tmp files.")]
        internal static partial void LogFailedToCleanupOrphanTmpFiles(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete orphaned offload metadata. key={Key}")]
        internal static partial void LogFailedToDeleteOrphanedOffloadMetadata(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string key);

    }
}
