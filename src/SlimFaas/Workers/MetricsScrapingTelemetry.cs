using Prometheus;

namespace SlimFaas.Workers;

internal static class MetricsScrapingTelemetry
{
    private static readonly Histogram Duration = Metrics.CreateHistogram(
        "slimfaas_metrics_scrape_duration_seconds",
        "Duration of one function metrics target scrape.",
        new HistogramConfiguration
        {
            LabelNames = ["function"],
            Buckets = Histogram.ExponentialBuckets(0.005, 2, 12)
        });

    private static readonly Counter Failures = Metrics.CreateCounter(
        "slimfaas_metrics_scrape_failures_total",
        "Number of function metrics scrape failures.",
        "function",
        "reason");

    private static readonly Gauge LastSuccess = Metrics.CreateGauge(
        "slimfaas_metrics_scrape_last_success_unixtime",
        "Unix timestamp of the latest successful function metrics scrape.",
        "function");

    public static void RecordDuration(string function, TimeSpan duration) =>
        Duration.WithLabels(function).Observe(duration.TotalSeconds);

    public static void RecordFailure(string function, string reason) =>
        Failures.WithLabels(function, reason).Inc();

    public static void RecordSuccess(string function) =>
        LastSuccess.WithLabels(function).Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
}
