namespace SlimFaas.WebSocket;

internal static partial class WebSocketClusterDirectoryLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "WebSocket directory maintenance failed.")]
    internal static partial void LogDirectoryMaintenanceFailed(this ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSocket directory mutation rejected: {Status} {Error}")]
    internal static partial void LogDirectoryMutationRejected(this ILogger logger, int status, string? error);
}
