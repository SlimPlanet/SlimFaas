// LogTool: [LoggerMessage] definitions for FunctionEndpointsHelpers.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class FunctionEndpointsHelpersLogMessages
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "PathStartWithVisibility {PathStartWith} should be prefixed by Public: or Private:")]
        internal static partial void LogPathStartWithVisibilityShouldBePrefixedByPublic(this global::Microsoft.Extensions.Logging.ILogger logger, global::SlimFaas.Kubernetes.PathVisibility pathStartWith);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "RemoteIp: {RemoteIp}")]
        internal static partial void LogRemoteIp(this global::Microsoft.Extensions.Logging.ILogger logger, string remoteIp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "PodIp: {PodIp}")]
        internal static partial void LogPodIp(this global::Microsoft.Extensions.Logging.ILogger logger, string podIp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Request come from internal namespace RemoteIp: {RemoteIp}")]
        internal static partial void LogRequestComeFromInternalNamespace(this global::Microsoft.Extensions.Logging.ILogger logger, string remoteIp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Request come from external namespace RemoteIp: {RemoteIp}")]
        internal static partial void LogRequestComeFromExternalNamespace(this global::Microsoft.Extensions.Logging.ILogger logger, string remoteIp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Request body offload check. ShouldOffload={ShouldOffload} ContentLength={ContentLength} Threshold={Threshold}")]
        internal static partial void LogRequestBodyOffloadCheckShouldOffloadContentLength(this global::Microsoft.Extensions.Logging.ILogger logger, bool shouldOffload, long? contentLength, long threshold);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Offloading request metadata. MetaKey={MetaKey} Tags={Tags}")]
        internal static partial void LogOffloadingRequestMetadataMetaKeyTags(this global::Microsoft.Extensions.Logging.ILogger logger, string metaKey, string tags);

    }
}
