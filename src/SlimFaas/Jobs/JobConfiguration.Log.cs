// LogTool: [LoggerMessage] definitions for JobConfiguration.cs (#358, phase 3).
namespace SlimFaas.Jobs
{
    internal static partial class JobConfigurationLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "JobConfiguration: {Json}")]
        internal static partial void LogJobConfiguration(this global::Microsoft.Extensions.Logging.ILogger logger, string json);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error parsing SlimFaas job configuration")]
        internal static partial void LogErrorParsingSlimFaasJobConfiguration(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
