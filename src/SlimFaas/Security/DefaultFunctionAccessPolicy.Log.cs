// LogTool: [LoggerMessage] definitions for DefaultFunctionAccessPolicy.cs (#358, phase 3).
namespace SlimFaas.Security
{
    internal static partial class DefaultFunctionAccessPolicyLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "IsInternalRequest={IsInternal} Remote={RemoteIp}")]
        internal static partial void LogIsInternalRequestRemote(this global::Microsoft.Extensions.Logging.ILogger logger, bool isInternal, string? remoteIp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Caller {Caller} (Remote={RemoteIp}) is not in SlimFaas/AllowedCallers of {Function}")]
        internal static partial void LogCallerNotAllowed(this global::Microsoft.Extensions.Logging.ILogger logger, string caller, string? remoteIp, string function);

    }
}
