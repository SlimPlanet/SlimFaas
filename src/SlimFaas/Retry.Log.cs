// LogTool: [LoggerMessage] definitions for Retry.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class RetryLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Try {Attempt} : wait number {Delay} second")]
        internal static partial void LogTryWaitNumberSecond(this global::Microsoft.Extensions.Logging.ILogger logger, int attempt, int delay);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "SlimData Service DoAsync")]
        internal static partial void LogSlimDataServiceDoAsync(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Try {Attempt} : wait number {Delay} second")]
        internal static partial void LogTryWaitNumberSecond2(this global::Microsoft.Extensions.Logging.ILogger logger, int attempt, int delay);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "SlimData Service DoAsync")]
        internal static partial void LogSlimDataServiceDoAsync2(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "DoRequestAsync Try {Attempt} : wait number {Delay} second")]
        internal static partial void LogDoRequestAsyncTryWaitNumberSecond(this global::Microsoft.Extensions.Logging.ILogger logger, int attempt, int delay);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Network exception")]
        internal static partial void LogNetworkException(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Net.Http.HttpRequestException exception);

    }
}
