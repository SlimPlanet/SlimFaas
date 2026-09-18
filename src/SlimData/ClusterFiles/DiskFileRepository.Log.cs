// LogTool: [LoggerMessage] definitions for DiskFileRepository.cs (#358, phase 3).
namespace SlimData.ClusterFiles
{
    internal static partial class DiskFileRepositoryLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to decode Base64 filename, skipping. path={Path}")]
        internal static partial void LogFailedToDecodeBase64FilenameSkipping(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to read metadata file, skipping. path={Path}")]
        internal static partial void LogFailedToReadMetadataFileSkipping(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete temporary file. path={Path}")]
        internal static partial void LogFailedToDeleteTemporaryFilePath(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to delete orphan .tmp file. path={Path}")]
        internal static partial void LogFailedToDeleteOrphanTmpFile(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string path);

    }
}
