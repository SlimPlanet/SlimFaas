using SlimData;

namespace SlimFaas.Streams;

internal static partial class StreamServiceLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Stream {Stream} persistence failed for {Action}")]
    internal static partial void LogStreamPersistenceFailure(this ILogger logger, Exception exception, string stream, DurableStreamAction action);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Stream {Stream} rejected {Action}: HTTP {Status}: {Error}")]
    internal static partial void LogStreamMutationRejected(this ILogger logger, string stream, DurableStreamAction action, int status, string? error);
    [LoggerMessage(Level = LogLevel.Error, Message = "Stream dispatcher failed")]
    internal static partial void LogStreamDispatcherFailed(this ILogger logger, Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Stream {Stream} configuration rejected for {Function}: {Error}")]
    internal static partial void LogStreamConfigurationRejected(this ILogger logger, string stream, string function, string? error);
}
