// LogTool: [LoggerMessage] definitions for DataFileRoutes.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class DataFileRoutesLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Missing/invalid Content-Length for /data/files. Using default={DefaultBytes} bytes for limiter. Id={Id}")]
        internal static partial void LogMissingInvalidContentLengthForData(this global::Microsoft.Extensions.Logging.ILogger logger, long defaultBytes, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to delete local file after metadata deletion. Id={Id}")]
        internal static partial void LogUnableToDeleteLocalFileAfter(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string id);

    }
}
