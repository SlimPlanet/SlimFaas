// LogTool: [LoggerMessage] definitions for ClusterFileSync.cs (#358, phase 3).
namespace SlimData.ClusterFiles
{
    internal static partial class ClusterFileSyncLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "FileSync announce skipped (remote returned 501 Not Implemented). Node={Node}")]
        internal static partial void LogFileSyncAnnounceSkippedRemoteReturned501(this global::Microsoft.Extensions.Logging.ILogger logger, string node);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "FileSync announce failed. Node={Node}")]
        internal static partial void LogFileSyncAnnounceFailedNode(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string node);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "GET {Node}")]
        internal static partial void LogGET(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri node);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "GET {FileUri} {StatusCode}")]
        internal static partial void LogGET2(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri fileUri, global::System.Net.HttpStatusCode statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "HEAD failed on node {Node}. Status={Status}")]
        internal static partial void LogHEADFailedOnNodeStatus(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri node, int status);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "HEAD ok but no Content-Length from node {Node}")]
        internal static partial void LogHEADOkButNoContentLength(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri node);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Invalid tags header from node {Node}. Id={Id}")]
        internal static partial void LogInvalidTagsHeaderFromNodeId(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri node, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "GET {FileUri} {StatusCode}. Length={Len} ContentType={ContentType} ExpireAtUtcTicks={ExpireAtUtcTicks} Tags={Tags}")]
        internal static partial void LogGETLengthContentTypeExpireAtUtcTicksTags(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri fileUri, global::System.Net.HttpStatusCode statusCode, long len, string contentType, long? expireAtUtcTicks, string tags);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Cluster pull integrity mismatch from {Node}. Id={Id} ExpectedSha={Sha} ActualSha={ActSha} ExpectedLen={Len} ActualLen={ActLen}")]
        internal static partial void LogClusterPullIntegrityMismatchFromId(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri node, string id, string sha, string actSha, long len, long actLen);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Range pull failed from node {Node}. Id={Id}")]
        internal static partial void LogRangePullFailedFromNodeId(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string node, string id);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "File delete signal skipped because the remote node does not support it. Node={Node}")]
        internal static partial void LogFileDeleteSignalSkippedBecauseThe(this global::Microsoft.Extensions.Logging.ILogger logger, string node);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "File delete signal failed. Node={Node} Id={Id}")]
        internal static partial void LogFileDeleteSignalFailedNodeId(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string node, string id);

    }
}
