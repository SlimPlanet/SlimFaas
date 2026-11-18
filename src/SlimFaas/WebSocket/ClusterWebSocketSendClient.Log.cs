namespace SlimFaas.WebSocket;

internal static partial class ClusterWebSocketSendClientLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSocket peer {Operation} delivery failed for {FunctionName}.")]
    internal static partial void LogPeerDeliveryFailed(this ILogger logger, Exception exception, string operation, string functionName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSocket event delivery rejected for {FunctionName} by {Node}: {Status}")]
    internal static partial void LogPeerEventRejected(this ILogger logger, string functionName, string node, int status);
}
