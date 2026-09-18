// LogTool: [LoggerMessage] definitions for SlimJobsWorker.cs (#358, phase 3).
namespace SlimFaas.Jobs
{
    internal static partial class SlimJobsWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global error in slimFaas jobs worker")]
        internal static partial void LogGlobalErrorInSlimFaasJobsWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in SlimJobsWorker")]
        internal static partial void LogErrorInSlimJobsWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Job worker error")]
        internal static partial void LogJobWorkerError(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
