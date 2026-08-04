using System.Diagnostics;
using Prometheus;

namespace SlimFaas.Workers;

internal static class AsyncQueueTelemetry
{
    private static readonly Counter Wakeups = Metrics.CreateCounter(
        "slimfaas_async_dispatch_wake_total",
        "Number of async dispatch worker wake-ups.",
        new CounterConfiguration { LabelNames = ["transport", "cause"] });

    private static readonly Histogram CycleDuration = Metrics.CreateHistogram(
        "slimfaas_async_worker_cycle_duration_seconds",
        "Duration of an async queue worker cycle.",
        new HistogramConfiguration
        {
            LabelNames = ["transport"],
            Buckets = Histogram.ExponentialBuckets(0.0005, 2, 16)
        });

    private static readonly Histogram SnapshotDuration = Metrics.CreateHistogram(
        "slimfaas_async_queue_snapshot_duration_seconds",
        "Duration of a lightweight async queue state snapshot.",
        new HistogramConfiguration
        {
            LabelNames = ["transport"],
            Buckets = Histogram.ExponentialBuckets(0.0001, 2, 16)
        });

    private static readonly Histogram DequeueDuration = Metrics.CreateHistogram(
        "slimfaas_async_dequeue_duration_seconds",
        "Duration of a durable async queue dequeue.",
        new HistogramConfiguration
        {
            LabelNames = ["transport"],
            Buckets = Histogram.ExponentialBuckets(0.0005, 2, 16)
        });

    public static void RecordWake(string transport, string cause) =>
        Wakeups.WithLabels(transport, cause).Inc();

    public static IDisposable MeasureCycle(string transport) =>
        CycleDuration.WithLabels(transport).NewTimer();

    public static IDisposable MeasureSnapshot(string transport) =>
        SnapshotDuration.WithLabels(transport).NewTimer();

    public static IDisposable MeasureDequeue(string transport) =>
        DequeueDuration.WithLabels(transport).NewTimer();
}
