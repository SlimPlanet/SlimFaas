// LogTool: [LoggerMessage] definitions for JobScheduleEndpoints.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class JobScheduleEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Create job {JobName} with {ScheduleCreateJob}")]
        internal static partial void LogCreateJobWith(this global::Microsoft.Extensions.Logging.ILogger logger, string jobName, global::SlimFaas.Kubernetes.ScheduleCreateJob scheduleCreateJob);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Delete job schedule {JobName} with {Id}")]
        internal static partial void LogDeleteJobScheduleWith(this global::Microsoft.Extensions.Logging.ILogger logger, string jobName, string id);

    }
}
