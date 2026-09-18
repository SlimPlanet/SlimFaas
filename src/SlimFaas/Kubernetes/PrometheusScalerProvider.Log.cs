// LogTool: [LoggerMessage] definitions for PrometheusScalerProvider.cs (#358, phase 3).
namespace SlimFaas.Kubernetes
{
    internal static partial class PrometheusScalerProviderLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Cannot evaluate scaling metric {Metric} for function {Function}")]
        internal static partial void LogCannotEvaluateScalingMetricForFunction(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string metric, string function);

    }
}
