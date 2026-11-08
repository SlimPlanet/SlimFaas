namespace SlimFaas.Local;

internal static partial class LocalJobManagerLog
{
    [LoggerMessage(Level = LogLevel.Error,
        Message = "Local job start failed. Job={Job} ElementId={ElementId} Error={Error}")]
    internal static partial void LogLocalJobStartFailed(this ILogger logger, string job, string elementId, string error);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Local job state storage is unavailable. Run={Run} Error={Error}. The durable plan is retained; pending state will be reconciled by subsequent monitor cycles.")]
    internal static partial void LogLocalJobPersistenceUnavailable(this ILogger logger, string run, string error);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Local job state storage resumed. Run={Run} FailedOperations={FailedOperations}.")]
    internal static partial void LogLocalJobPersistenceResumed(this ILogger logger, string run, long failedOperations);
}
