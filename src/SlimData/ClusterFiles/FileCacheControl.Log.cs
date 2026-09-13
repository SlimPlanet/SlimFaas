// LogTool: [LoggerMessage] definitions for FileCacheControl.cs (#358, phase 3).
namespace SlimData.ClusterFiles
{
    internal static partial class LinuxFileCacheControlLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unable to advise Linux to release file cache. Result={Result}")]
        internal static partial void LogUnableToAdviseLinuxToRelease(this global::Microsoft.Extensions.Logging.ILogger logger, int result);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unable to advise Linux to release file cache.")]
        internal static partial void LogUnableToAdviseLinuxToRelease2(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
