// LogTool: [LoggerMessage] definitions for StatusStreamEndpoints.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class StatusStreamEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Status stream client disconnected.")]
        internal static partial void LogStatusStreamClientDisconnected(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.OperationCanceledException exception);

    }
}
