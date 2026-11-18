namespace SlimFaas;

internal static partial class FileAdmissionReconcilerLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "File reference reconciliation failed.")]
    internal static partial void LogFileReferenceReconciliationFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Referenced file is unavailable from known peers. id={Id}")]
    internal static partial void LogReferencedFileUnavailable(this ILogger logger, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Repaired referenced file. id={Id} length={Length}")]
    internal static partial void LogReferencedFileRepaired(this ILogger logger, string id, long length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Referenced file repair or retirement failed. id={Id}")]
    internal static partial void LogReferencedFileRepairFailed(this ILogger logger, Exception exception, string id);
}
