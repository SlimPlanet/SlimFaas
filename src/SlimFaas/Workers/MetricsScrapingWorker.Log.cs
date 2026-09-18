// LogTool: [LoggerMessage] definitions for MetricsScrapingWorker.cs (#358, phase 3).
namespace SlimFaas.Workers
{
    internal static partial class MetricsScrapingWorkerLog
    {
        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Error, Message = "Global error in MetricsScrapingWorker")]
        internal static partial void LogGlobalErrorInMetricsScrapingWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unexpected error during delay in MetricsScrapingWorker")]
        internal static partial void LogUnexpectedErrorDuringDelayInMetricsScrapingWorker(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Information, Message = "Scraping metrics for deployment {Deployment} with {TargetCount} targets")]
        internal static partial void LogScrapingMetricsForDeploymentWithTargets(this global::Microsoft.Extensions.Logging.ILogger logger, string deployment, int targetCount);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Metrics scrape rejected for {Url}: Content-Length {ContentLength} exceeds MaxResponseBytes {MaxResponseBytes}")]
        internal static partial void LogMetricsScrapeRejectedForContentLength(this global::Microsoft.Extensions.Logging.ILogger logger, string url, long? contentLength, long maxResponseBytes);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Metrics scrape rejected for {Url}: Reason={Reason}, BytesRead={BytesRead}, LinesRead={LinesRead}")]
        internal static partial void LogMetricsScrapeRejectedForReasonBytesRead(this global::Microsoft.Extensions.Logging.ILogger logger, string url, global::SlimFaas.Workers.PrometheusStreamParseStatus reason, long bytesRead, long linesRead);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Metrics scrape timed out after {TimeoutSeconds} seconds for {Url}")]
        internal static partial void LogMetricsScrapeTimedOutAfterSeconds(this global::Microsoft.Extensions.Logging.ILogger logger, int timeoutSeconds, string url);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "metrics scrape error for {Url}")]
        internal static partial void LogMetricsScrapeErrorFor(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception, string url);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to persist metrics store to database")]
        internal static partial void LogUnableToPersistMetricsStoreTo(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

        [global::Microsoft.Extensions.Logging.LoggerMessage(Level = global::Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Unable to hydrate metrics store from database")]
        internal static partial void LogUnableToHydrateMetricsStoreFrom(this global::Microsoft.Extensions.Logging.ILogger logger, global::System.Exception exception);

    }
}
