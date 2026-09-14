// LogTool: [LoggerMessage] definitions for AutoScaler.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class AutoScalerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Scaling provider failed for {Function}")]
        internal static partial void LogScalingProviderFailedFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string function);

    }
}
