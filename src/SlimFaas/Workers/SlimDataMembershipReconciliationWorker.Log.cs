// LogTool: [LoggerMessage] definitions for SlimDataMembershipReconciliationWorker.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class SlimDataMembershipReconciliationWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimDataMembershipReconciliationWorker: Start")]
        internal static partial void LogSlimDataMembershipReconciliationWorkerStart(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Deferring SlimData membership removals because the orchestrator topology is incomplete. RequestedReplicas={RequestedReplicas}, EligibleEndpoints={EligibleEndpoints}")]
        internal static partial void LogDeferringSlimDataMembershipRemovals(this global::Microsoft.Extensions.Logging.ILogger logger, int requestedReplicas, int eligibleEndpoints);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in SlimDataMembershipReconciliationWorker")]
        internal static partial void LogErrorInSlimDataMembershipReconciliationWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Adding missing SlimData Raft member. Endpoint={Endpoint}")]
        internal static partial void LogAddingMissingSlimDataRaftMemberEndpoint(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri endpoint);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData Raft member was not added and will be retried. Endpoint={Endpoint}")]
        internal static partial void LogSlimDataRaftMemberWasNotAdded(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri endpoint);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Skipping SlimData membership removals because the local endpoint is absent from the orchestrator snapshot. LocalEndpoint={LocalEndpoint}")]
        internal static partial void LogSkippingSlimDataMembershipRemovalsBecauseThe(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri localEndpoint);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Removing stale SlimData Raft member. Endpoint={Endpoint}, MissingCycles={MissingCycles}")]
        internal static partial void LogRemovingStaleSlimDataRaftMemberEndpoint(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri endpoint, int missingCycles);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimData Raft member was not removed and will be retried. Endpoint={Endpoint}")]
        internal static partial void LogSlimDataRaftMemberWasNotRemoved(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Uri endpoint);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Ignoring invalid SlimData endpoint for pod {PodName}")]
        internal static partial void LogIgnoringInvalidSlimDataEndpointForPod(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.UriFormatException exception, string podName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Ignoring SlimData Raft member without an HTTP endpoint. Endpoint={Endpoint}")]
        internal static partial void LogIgnoringSlimDataRaftMemberWithoutAn(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.EndPoint endpoint);

    }
}
