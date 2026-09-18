// LogTool: [LoggerMessage] definitions for DataVisibilityEndpointFilter.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class DataVisibilityEndpointFilterLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Denied /data access (DefaultVisibility=Private) for Remote={RemoteIp}")]
        internal static partial void LogDeniedDataAccessDefaultVisibilityPrivateFor(this global::Microsoft.Extensions.Logging.ILogger logger, string? remoteIp);

    }
}
