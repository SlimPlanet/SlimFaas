// LogTool: [LoggerMessage] definitions for Endpoints.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class EndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData is unavailable for {Path}")]
        internal static partial void LogSlimDataIsUnavailableFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimData.SlimDataUnavailableException exception, global::Microsoft.AspNetCore.Http.PathString path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Invalid SlimData request for {Path}")]
        internal static partial void LogInvalidSlimDataRequestFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.IO.InvalidDataException exception, global::Microsoft.AspNetCore.Http.PathString path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Unexpected error on {Path}")]
        internal static partial void LogUnexpectedErrorOn(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, global::Microsoft.AspNetCore.Http.PathString path);

    }
}
