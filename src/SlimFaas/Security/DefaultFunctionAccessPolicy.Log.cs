// LogTool: [LoggerMessage] definitions for DefaultFunctionAccessPolicy.cs (#358, phase 3).
namespace SlimFaas.Security
{
    internal static partial class DefaultFunctionAccessPolicyLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "IsInternalRequest={IsInternal} Remote={RemoteIp}")]
        internal static partial void LogIsInternalRequestRemote(this global::Microsoft.Extensions.Logging.ILogger logger, bool isInternal, string? remoteIp);

    }
}
