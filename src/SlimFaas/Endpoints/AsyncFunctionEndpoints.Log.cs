namespace SlimFaas.Endpoints;

internal static partial class AsyncFunctionEndpointsLog
{
    [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Trace,
        Message = "Async admission stage={Stage} Function={FunctionName} ElementId={ElementId} ServerRequestId={ServerRequestId} FileId={FileId}")]
    internal static partial void LogAsyncAdmissionStage(this global::Microsoft.Extensions.Logging.ILogger logger,
        string stage, string functionName, string elementId, string serverRequestId, string? fileId);
}
