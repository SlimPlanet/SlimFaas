// LogTool: [LoggerMessage] definitions for WakeUpFunction.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class WakeUpFunctionLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "1: Waking up function {FunctionName} {SetTickLastCall}")]
        internal static partial void Log1WakingUpFunction(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, long setTickLastCall);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Function {FunctionName} not found after delay")]
        internal static partial void LogFunctionNotFoundAfterDelay(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "2: Waking up function {FunctionName} {SetTickLastCall}")]
        internal static partial void Log2WakingUpFunction(this global::Microsoft.Extensions.Logging.ILogger logger, string functionName, long setTickLastCall);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error in wake up function")]
        internal static partial void LogErrorInWakeUpFunction(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
