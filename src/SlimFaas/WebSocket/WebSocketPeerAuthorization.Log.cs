namespace SlimFaas.WebSocket;

internal static partial class WebSocketPeerAuthorizationLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "WebSocket peer {Operation} failed for {FunctionName}.")]
    internal static partial void LogRelayOperationFailed(this ILogger logger, Exception exception, string operation, string functionName);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Rejected WebSocket peer relay: {Reason}")]
    internal static partial void LogRelayRejected(this ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invalid WebSocket peer relay proof.")]
    internal static partial void LogRelayProofInvalid(this ILogger logger, Exception exception);
}
