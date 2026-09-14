// LogTool: [LoggerMessage] definitions for SlimFaasClient.cs (#358, phase 3).
namespace SlimFaasKafka.Services
{
    internal static partial class SlimFaasClientLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Sending wake up request to SlimFaas for function {Function}")]
        internal static partial void LogSendingWakeUpRequestToSlimFaas(this global::Microsoft.Extensions.Logging.ILogger logger, string function);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Wake up for {Function} succeeded with status code {StatusCode}")]
        internal static partial void LogWakeUpForSucceededWithStatus(this global::Microsoft.Extensions.Logging.ILogger logger, string function, global::System.Net.HttpStatusCode statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Wake up for {Function} failed with status code {StatusCode}")]
        internal static partial void LogWakeUpForFailedWithStatus(this global::Microsoft.Extensions.Logging.ILogger logger, string function, global::System.Net.HttpStatusCode statusCode);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error while calling SlimFaas wake up for function {Function}")]
        internal static partial void LogErrorWhileCallingSlimFaasWakeUp(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string function);

    }
}
