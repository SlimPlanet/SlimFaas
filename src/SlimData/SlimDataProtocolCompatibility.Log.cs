// LogTool: [LoggerMessage] definitions for SlimDataProtocolCompatibility.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class SlimDataProtocolCompatibilityLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimData Raft protocol is compatible. Protocol={Protocol}, Leader={Leader}, Reason={Reason}")]
        internal static partial void LogSlimDataRaftProtocolIsCompatibleProtocol(this global::Microsoft.Extensions.Logging.ILogger logger, string protocol, global::System.Uri? leader, string reason);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData Raft protocol is incompatible or unavailable. ExpectedProtocol={Protocol}, Leader={Leader}, Reason={Reason}")]
        internal static partial void LogSlimDataRaftProtocolIsIncompatibleOr(this global::Microsoft.Extensions.Logging.ILogger logger, string protocol, global::System.Uri? leader, string reason);

    }
}
namespace SlimData
{
    internal static partial class SlimDataProtocolCompatibilityWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Unable to verify the SlimData leader protocol")]
        internal static partial void LogUnableToVerifyTheSlimDataLeader(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
