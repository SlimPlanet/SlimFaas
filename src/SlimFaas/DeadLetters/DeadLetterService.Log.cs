using SlimData;

namespace SlimFaas.DeadLetters;

internal static partial class DeadLetterServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "DLQ {Transition} for {Function}: message {DeadLetterId}, original {OriginalElementId}, replay {ElementId}.")]
    internal static partial void LogDeadLetterTransition(this ILogger logger, string transition, string function, string deadLetterId, string originalElementId, string? elementId);

    [LoggerMessage(Level = LogLevel.Error, Message = "DLQ persistence failed for {Function}, action {Action}. The request remains retained.")]
    internal static partial void LogDeadLetterPersistenceFailure(this ILogger logger, Exception exception, string function, DeadLetterAction action);

    [LoggerMessage(Level = LogLevel.Error, Message = "DLQ maintenance failed; durable state will be retried.")]
    internal static partial void LogDeadLetterMaintenanceFailure(this ILogger logger, Exception exception);
}
