// LogTool: [LoggerMessage] definitions for ClusterFileAnnounceWorker.cs (#358, phase 3).
namespace SlimData.ClusterFiles
{
    internal static partial class ClusterFileAnnounceWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Auto-pull failed. Id={Id} Sha={Sha}")]
        internal static partial void LogAutoPullFailedIdSha(this global::Microsoft.Extensions.Logging.ILogger logger, string id, string sha);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Failed to auto-pull announced file. Id={Id}")]
        internal static partial void LogFailedToAutoPullAnnouncedFile(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string id);

    }
}
