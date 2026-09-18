// LogTool: [LoggerMessage] definitions for JobEndpoints.cs (#358, phase 3).
namespace SlimFaas.Endpoints
{
    internal static partial class JobEndpointsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Invalid function name: {FunctionName}. Must match pattern [a-z0-9_-] and be between 3 and 30 characters")]
        internal static partial void LogInvalidFunctionNameMustMatchPattern(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Create job {JobName} with {CreateJob}")]
        internal static partial void LogCreateJobWith(this global::Microsoft.Extensions.Logging.ILogger logger, string jobName, global::SlimFaas.Kubernetes.CreateJob createJob);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Debug, Message = "Create job details {CreateJob} ")]
        internal static partial void LogCreateJobDetails(this global::Microsoft.Extensions.Logging.ILogger logger, string createJob);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Job HTTP Status {HttpStatusCode} with error {ErrorKey}")]
        internal static partial void LogJobHTTPStatusWithError(this global::Microsoft.Extensions.Logging.ILogger logger, int httpStatusCode, string? errorKey);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Delete job {JobName} with {Id}")]
        internal static partial void LogDeleteJobWith(this global::Microsoft.Extensions.Logging.ILogger logger, string jobName, string id);

    }
}
