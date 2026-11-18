namespace SlimFaas.Local;

internal static partial class LocalSupervisorLog
{
    [LoggerMessage(Level = LogLevel.Critical,
        Message = "Local supervision failed before cleanup. Error={Error}")]
    internal static partial void LogLocalSupervisionFailedBeforeCleanup(this ILogger logger, string error);
}
