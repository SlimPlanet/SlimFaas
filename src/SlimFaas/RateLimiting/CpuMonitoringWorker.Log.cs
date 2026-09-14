// LogTool: [LoggerMessage] definitions for CpuMonitoringWorker.cs (#358, phase 3).
namespace SlimFaas.RateLimiting
{
    internal static partial class CpuMonitoringWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "CPU monitoring is disabled")]
        internal static partial void LogCPUMonitoringIsDisabled(this global::Microsoft.Extensions.Logging.ILogger logger);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "CPU monitoring started. Interval: {IntervalMs}ms, High: {High}%, Low: {Low}%")]
        internal static partial void LogCPUMonitoringStartedIntervalMsHigh(this global::Microsoft.Extensions.Logging.ILogger logger, int intervalMs, double high, double low);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Error monitoring CPU usage")]
        internal static partial void LogErrorMonitoringCPUUsage(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "CPU rate limiting activated. CPU: {CpuPercent:F2}%, Threshold: {Threshold}%")]
        internal static partial void LogCPURateLimitingActivatedCPUThreshold(this global::Microsoft.Extensions.Logging.ILogger logger, double cpuPercent, double threshold);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "CPU rate limiting deactivated. CPU: {CpuPercent:F2}%, Threshold: {Threshold}%")]
        internal static partial void LogCPURateLimitingDeactivatedCPUThreshold(this global::Microsoft.Extensions.Logging.ILogger logger, double cpuPercent, double threshold);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "High CPU usage detected: {CpuPercent:F2}%")]
        internal static partial void LogHighCPUUsageDetected(this global::Microsoft.Extensions.Logging.ILogger logger, double cpuPercent);

    }
}
