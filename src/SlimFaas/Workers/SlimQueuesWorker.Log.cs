// LogTool: [LoggerMessage] definitions for SlimQueuesWorker.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class SlimQueuesWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global Error in SlimFaas Worker")]
        internal static partial void LogGlobalErrorInSlimFaasWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "All pods saturated for {FunctionDeployment}, skipping dequeue")]
        internal static partial void LogAllPodsSaturatedForSkippingDequeue(this global::Microsoft.Extensions.Logging.ILogger logger, string functionDeployment);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "{CustomRequestMethod}: {CustomRequestPath}{CustomRequestQuery} Sending")]
        internal static partial void LogSending(this global::Microsoft.Extensions.Logging.ILogger logger, string customRequestMethod, string customRequestPath, string customRequestQuery);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to deserialize async request. FunctionName={FunctionName} QueueElementId={QueueElementId}")]
        internal static partial void LogUnableToDeserializeAsyncRequestFunctionName(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName, string queueElementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Loaded offloaded metadata. MetaKey={MetaKey} Tags={Tags}")]
        internal static partial void LogLoadedOffloadedMetadataMetaKeyTags(this global::Microsoft.Extensions.Logging.ILogger logger, string metaKey, string? tags);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to load offloaded body for id={FileId}. QueueElementId={QueueElementId}; reporting HTTP 500 to the queue")]
        internal static partial void LogUnableToLoadOffloadedBodyFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string fileId, string queueElementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Async request failed for {FunctionName}/{ElementId}")]
        internal static partial void LogAsyncRequestFailedFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string functionName, string elementId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "{CustomRequestMethod}: /async-function{CustomRequestPath}{CustomRequestQuery} {StatusCode}")]
        internal static partial void LogAsyncFunction(this global::Microsoft.Extensions.Logging.ILogger logger, string customRequestMethod, string customRequestPath, string customRequestQuery, int statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to clean terminal async offload. FileId={FileId}")]
        internal static partial void LogUnableToCleanTerminalAsyncOffload(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string fileId);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to load offloaded body for id={FileId}: {Reason}. QueueElementId={QueueElementId}; reporting HTTP 500 to the queue")]
        internal static partial void LogUnableToLoadOffloadedBodyFor2(this global::Microsoft.Extensions.Logging.ILogger logger, string fileId, string reason, string queueElementId);

    }
}
