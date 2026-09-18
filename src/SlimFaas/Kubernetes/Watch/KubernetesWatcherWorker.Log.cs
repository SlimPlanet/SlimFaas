// LogTool: [LoggerMessage] definitions for KubernetesWatcherWorker.cs (#358, phase 3).
namespace SlimFaas.Kubernetes.Watch
{
    internal static partial class KubernetesWatcherWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Watch stream {Target} unavailable (HTTP {StatusCode}): falling back to the legacy polling cadence until the stream is restored. Check that the service account grants the \"watch\" verb on {Target}")]
        internal static partial void LogWatchStreamUnavailableFallingBackToPolling(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception? exception, string target, int? statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "KubernetesWatcherWorker disabled: the orchestrator is not the Kubernetes implementation")]
        internal static partial void LogKubernetesWatcherWorkerDisabledTheOrchestratorIsNot(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "KubernetesWatcherWorker starting: watching pods/deployments/statefulsets/jobs/cronjobs in namespace {Namespace}")]
        internal static partial void LogKubernetesWatcherWorkerStartingWatchingPodsDeploymentsStatefulsets(this global::Microsoft.Extensions.Logging.ILogger logger, string @namespace);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Watch stream {Target} received HTTP 410 Gone: resetting resourceVersion and signaling a resync")]
        internal static partial void LogWatchStreamReceivedHTTP410Gone(this global::Microsoft.Extensions.Logging.ILogger logger, string target);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Watch stream {Target} received an ERROR event (code {Code}): reconnecting")]
        internal static partial void LogWatchStreamReceivedAnERROREvent(this global::Microsoft.Extensions.Logging.ILogger logger, string target, int? code);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Watch stream {Target}: ignoring unknown line")]
        internal static partial void LogWatchStreamIgnoringUnknownLine(this global::Microsoft.Extensions.Logging.ILogger logger, string target);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Watch stream {Target} still unavailable (HTTP {StatusCode}): retrying after backoff")]
        internal static partial void LogWatchStreamStillUnavailableHTTPRetrying(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception? exception, string target, int? statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Watch stream {Target} restored: event-driven synchronization resumed")]
        internal static partial void LogWatchStreamRestoredEventDrivenSynchronization(this global::Microsoft.Extensions.Logging.ILogger logger, string target);

    }
}
