// LogTool: [LoggerMessage] definitions for ClusterMembershipCoordinator.cs (#358, phase 3).
namespace SlimData
{
    internal static partial class ClusterMembershipCoordinatorLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Message = "SlimData membership {Operation} completed. Endpoint={Endpoint}, Applied={Applied}, DurationMilliseconds={DurationMilliseconds}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}")]
        internal static partial void LogSlimDataMembershipCompletedEndpointAppliedDurationMilliseconds(this global::Microsoft.Extensions.Logging.ILogger logger, global::Microsoft.Extensions.Logging.LogLevel level, string operation, global::System.Uri endpoint, bool applied, double durationMilliseconds, long lastLogIndex, long committedLogIndex);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData member addition rejected because its command protocol is incompatible. Endpoint={Endpoint}, ExpectedProtocol={ExpectedProtocol}, ActualProtocol={ActualProtocol}, AssemblyVersion={AssemblyVersion}, Reason={Reason}")]
        internal static partial void LogSlimDataMemberAdditionRejectedBecauseIts(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri endpoint, string expectedProtocol, string? actualProtocol, string? assemblyVersion, string reason);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Adding SlimData member from a different compatible build during rolling update. Endpoint={Endpoint}, Protocol={Protocol}, LocalAssemblyVersion={LocalAssemblyVersion}, RemoteAssemblyVersion={RemoteAssemblyVersion}")]
        internal static partial void LogAddingSlimDataMemberFromDifferentCompatible(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri endpoint, string? protocol, string localAssemblyVersion, string remoteAssemblyVersion);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Starting SlimData membership {Operation}. Endpoint={Endpoint}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}, TimeoutSeconds={TimeoutSeconds}")]
        internal static partial void LogStartingSlimDataMembershipEndpointLastLogIndexCommittedLogIndex(this global::Microsoft.Extensions.Logging.ILogger logger, string operation, global::System.Uri endpoint, long lastLogIndex, long committedLogIndex, double timeoutSeconds);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData membership {Operation} timed out. Endpoint={Endpoint}, TimeoutSeconds={TimeoutSeconds}, DurationMilliseconds={DurationMilliseconds}")]
        internal static partial void LogSlimDataMembershipTimedOutEndpointTimeoutSeconds(this global::Microsoft.Extensions.Logging.ILogger logger, string operation, global::System.Uri endpoint, double timeoutSeconds, double durationMilliseconds);

    }
}
