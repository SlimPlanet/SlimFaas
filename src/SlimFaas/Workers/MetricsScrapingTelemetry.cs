using Prometheus;

namespace SlimFaas.Workers;

internal static class MetricsScrapingTelemetry
{
    private static readonly Histogram Duration = Metrics.CreateHistogram(
        "slimfaas_metrics_scrape_duration_seconds",
        "Duration of one function metrics target scrape.",
        new HistogramConfiguration
        {
            LabelNames = ["function", "source"],
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 12)
        });

    private static readonly Counter Failures = Metrics.CreateCounter(
        "slimfaas_metrics_scrape_failures_total",
        "Number of function metrics scrape failures.",
        "function",
        "source",
        "reason");

    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "slimfaas_metrics_scrape_last_success_unixtime",
        "Unix timestamp of the latest successful function metrics scrape.",
        "function",
        "source");

    private static readonly Gauge SourceAvailable = Metrics.CreateGauge(
        "slimfaas_metrics_source_available",
        "Whether the latest external or pod metrics source scrape succeeded.",
        "function",
        "source");

    public static void RecordDuration(string function, string source, TimeSpan duration) =>
        Duration.WithLabels(function, source).Observe(duration.TotalSeconds);

    public static void RecordFailure(string function, string source, string reason)
    {
        Failures.WithLabels(function, source, reason).Inc();
        SourceAvailable.WithLabels(function, source).Set(0);
    }

    public static void RecordSuccess(string function, string source)
    {
        LastSuccess.WithLabels(function, source).Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        SourceAvailable.WithLabels(function, source).Set(1);
    }
}
