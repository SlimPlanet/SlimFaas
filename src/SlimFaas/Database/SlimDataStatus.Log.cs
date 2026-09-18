// LogTool: [LoggerMessage] definitions for SlimDataStatus.cs (#358, phase 3).
namespace SlimFaas.Database
{
    internal static partial class SlimDataStatusLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Raft cluster is not ready, waiting for leader, consensus, local state and protocol compatibility. ProtocolReason={ProtocolReason}")]
        internal static partial void LogRaftClusterIsNotReadyWaiting(this global::Microsoft.Extensions.Logging.ILogger logger, string protocolReason);

    }
}
