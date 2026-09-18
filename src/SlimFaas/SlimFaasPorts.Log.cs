// LogTool: [LoggerMessage] definitions for SlimFaasPorts.cs (#358, phase 3).
namespace SlimFaas
{
    internal static partial class SlimFaasPortsLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "SlimFaas no ports found")]
        internal static partial void LogSlimFaasNoPortsFound(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "SlimFaasPorts: {Port}")]
        internal static partial void LogSlimFaasPorts(this global::Microsoft.Extensions.Logging.ILogger logger, int port);

    }
}
