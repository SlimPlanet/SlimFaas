// LogTool: [LoggerMessage] definitions for HealthWorker.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class HealthWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Raft cluster has no active consensus; the pod remains alive but is not ready")]
        internal static partial void LogRaftClusterHasNoActiveConsensus(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Raft cluster consensus is available again")]
        internal static partial void LogRaftClusterConsensusIsAvailableAgain(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global Error in HealthWorker")]
        internal static partial void LogGlobalErrorInHealthWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
