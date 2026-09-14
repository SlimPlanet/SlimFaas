// LogTool: [LoggerMessage] definitions for ClusterFileTransferRoutes.cs (#358, phase 3).
namespace SlimData.ClusterFiles.Http
{
    internal static partial class ClusterFileTransferRoutesLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "HEAD ok. Id={Id} Len={Len}")]
        internal static partial void LogHEADOkIdLen(this global::Microsoft.Extensions.Logging.ILogger logger, string id, long len);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "GET streaming (range enabled). Id={Id} Len={Len}")]
        internal static partial void LogGETStreamingRangeEnabledIdLen(this global::Microsoft.Extensions.Logging.ILogger logger, string id, long len);

    }
}
