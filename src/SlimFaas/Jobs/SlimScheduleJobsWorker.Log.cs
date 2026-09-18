// LogTool: [LoggerMessage] definitions for SlimScheduleJobsWorker.cs (#358, phase 3).
namespace SlimFaas.Jobs
{
    internal static partial class SlimScheduleJobsWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global error in SlimFaas schedule jobs worker")]
        internal static partial void LogGlobalErrorInSlimFaasScheduleJobs(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Checking schedule job {ScheduleId} in configuration {ConfigurationName} at timestamp {TimeStamp}")]
        internal static partial void LogCheckingScheduleJobInConfigurationAt(this global::Microsoft.Extensions.Logging.ILogger logger, string scheduleId, string configurationName, long timeStamp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Last execution timestamp for schedule job {ScheduleId} in configuration {ConfigurationName} is {LastExecutionTimeStamp}")]
        internal static partial void LogLastExecutionTimestampForScheduleJob(this global::Microsoft.Extensions.Logging.ILogger logger, string scheduleId, string configurationName, long lastExecutionTimeStamp);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Should run job for schedule {ScheduleId} in configuration {ConfigurationName}: {RunJob} at timestamp {LatestExecutionTimeStamp} (lastest: {LastestExecutionTimeStampFromDatabase})")]
        internal static partial void LogShouldRunJobForScheduleIn(this global::Microsoft.Extensions.Logging.ILogger logger, string scheduleId, string configurationName, bool runJob, long latestExecutionTimeStamp, long lastestExecutionTimeStampFromDatabase);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Enqueued job for schedule {ScheduleId} in configuration {ConfigurationName}")]
        internal static partial void LogEnqueuedJobForScheduleInConfiguration(this global::Microsoft.Extensions.Logging.ILogger logger, string scheduleId, string configurationName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Failed to enqueue job for schedule {ScheduleId} in configuration {ConfigurationName}: {Error}")]
        internal static partial void LogFailedToEnqueueJobForSchedule(this global::Microsoft.Extensions.Logging.ILogger logger, string scheduleId, string configurationName, string? error);

    }
}
