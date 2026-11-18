using Microsoft.Extensions.Logging;

internal static partial class TourEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid guided-tour request.")]
    internal static partial void LogInvalidTourRequest(this ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Guided-tour dependency unavailable for {TourId}.")]
    internal static partial void LogTourDependencyUnavailable(this ILogger logger, string tourId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Guided-tour {TourId} completed execution {ElementId}.")]
    internal static partial void LogTourCompleted(this ILogger logger, string tourId, string elementId);
}
