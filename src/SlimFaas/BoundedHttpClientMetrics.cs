using Prometheus;
using Prometheus.HttpClientMetrics;

namespace SlimFaas;

/// <summary>
/// Creates the prometheus-net HttpClient metrics without the destination host
/// label. Kubernetes pod IPs are ephemeral and prometheus-net collector
/// registries are append-only, so retaining one histogram child per historical
/// pod IP causes an unbounded cardinality and memory increase.
/// </summary>
public static class BoundedHttpClientMetrics
{
    private static readonly string[] RequestLabels = ["client"];
    private static readonly string[] ResponseLabels = ["client", "code"];

    public static HttpClientExporterOptions Create(CollectorRegistry? registry = null)
    {
        var factory = registry is null
            ? Metrics.DefaultFactory
            : Metrics.WithCustomRegistry(registry);
        var histogramConfiguration = new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.001, 2.0, 16)
        };

        return new HttpClientExporterOptions
        {
            InProgress =
            {
                Gauge = factory.CreateGauge(
                    "httpclient_requests_in_progress",
                    "Number of requests currently being executed by an HttpClient that have not yet received response headers.",
                    RequestLabels)
            },
            RequestCount =
            {
                Counter = factory.CreateCounter(
                    "httpclient_requests_sent_total",
                    "Count of HTTP requests that have been completed by an HttpClient.",
                    ResponseLabels)
            },
            RequestDuration =
            {
                Histogram = factory.CreateHistogram(
                    "httpclient_request_duration_seconds",
                    "Duration histogram of HTTP requests performed by an HttpClient.",
                    ResponseLabels,
                    histogramConfiguration)
            },
            ResponseDuration =
            {
                Histogram = factory.CreateHistogram(
                    "httpclient_response_duration_seconds",
                    "Duration histogram of HTTP requests performed by an HttpClient, measuring the duration until the HTTP response finished being processed.",
                    ResponseLabels,
                    histogramConfiguration)
            }
        };
    }
}
