// LogTool: [LoggerMessage] definitions for CallerAuthenticationMiddleware.cs and CallerClassification.cs.
namespace SlimFaas.Security
{
    internal static partial class CallerAuthenticationMiddlewareLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Signed request rejected: caller={CallerId} remote={RemoteIp} {Method} {Path}: {Reason}")]
        internal static partial void LogSignedRequestRejected(this global::Microsoft.Extensions.Logging.ILogger logger, string callerId, string remoteIp, string method, string path, string reason);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unsigned request from {RemoteIp} to {Path} accepted by the address rule (CallerAuthentication mode Hybrid). Sign it before switching to Strict.")]
        internal static partial void LogUnsignedInternalCallerAccepted(this global::Microsoft.Extensions.Logging.ILogger logger, string remoteIp, string path);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Caller authentication mode {Mode}, keys read from {SecretsDirectory}")]
        internal static partial void LogCallerAuthenticationConfigured(this global::Microsoft.Extensions.Logging.ILogger logger, string mode, string secretsDirectory);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Caller authentication mode {Mode} but the secrets directory {SecretsDirectory} does not exist: no signed request can be accepted until it is mounted.")]
        internal static partial void LogCallerSecretsDirectoryMissing(this global::Microsoft.Extensions.Logging.ILogger logger, string mode, string secretsDirectory);
    }
}
