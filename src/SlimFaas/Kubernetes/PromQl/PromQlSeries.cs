using SlimFaas.Workers;
using ZLinq;

namespace SlimFaas.Kubernetes;

internal sealed class EvalContext
{
    private readonly IReadOnlyDictionary<long,
        IReadOnlyDictionary<string,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string, double>>>>? _snapshot;
    private readonly IMetricsStore? _metricsStore;
    private readonly long _instantSelectorLookbackSeconds;
    private readonly string? _deployment;
    private readonly string? _externalSource;
    private readonly long? _requiredTimestamp;

    public readonly long Now;

    public EvalContext(
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> store,
        long now,
        TimeSpan instantSelectorLookback,
        string? deployment = null, string? externalSource = null, long? requiredTimestamp = null)
    {
        _snapshot = store;
        Now = now;
        _instantSelectorLookbackSeconds = ToLookbackSeconds(instantSelectorLookback);
        _deployment = deployment;
        _externalSource = externalSource;
        _requiredTimestamp = requiredTimestamp;
    }

    public EvalContext(
        IMetricsStore metricsStore,
        long now,
        TimeSpan instantSelectorLookback,
        string? deployment = null, string? externalSource = null, long? requiredTimestamp = null)
    {
        _metricsStore = metricsStore;
        Now = now;
        _instantSelectorLookbackSeconds = ToLookbackSeconds(instantSelectorLookback);
        _deployment = deployment;
        _externalSource = externalSource;
        _requiredTimestamp = requiredTimestamp;
    }

    public Dictionary<SeriesKey, double> Select(MetricSelector selector, SampleFunction function, long window)
    {
        long seconds = function == SampleFunction.Instant ? _instantSelectorLookbackSeconds : window;
        long start = Now < long.MinValue + seconds ? long.MinValue : Now - seconds;
        var result = new Dictionary<SeriesKey, double>();

        if (_requiredTimestamp is long required && (required <= start || required > Now)) return result;

        var merged = new Dictionary<SeriesKey, SortedList<long, MetricPoint>>();
        if (_metricsStore is null)
        {
            foreach (var (timestamp, deployments) in _snapshot!)
            {
                if (timestamp <= start || timestamp > Now) continue;
                foreach (var (deployment, pods) in deployments)
                foreach (var (pod, metrics) in pods)
                foreach (var (rawKey, value) in metrics)
                {
                    if (!TrySelect(selector, deployment, pod, rawKey, out var key)) continue;
                    if (!merged.TryGetValue(key, out var points)) merged[key] = points = [];
                    points[timestamp] = new(timestamp, value);
                }
            }
        }
        else
        {
            var seen = new HashSet<SeriesKey>();
            _metricsStore.VisitSeries((_, deployment, pod, rawKey, points) =>
            {
                int end = UpperBound(points, Now);
                if (end == 0 || points[end - 1].Timestamp <= start ||
                    !TrySelect(selector, deployment, pod, rawKey, out var key)) return;
                if (!seen.Add(key))
                {
                    // Exporters can change label order between scrapes. Merge just
                    // those series' selected points before computing a rate or average.
                    if (!merged.ContainsKey(key)) merged[key] = [];
                    result.Remove(key);
                    return;
                }

                if (_requiredTimestamp is long timestamp)
                {
                    int position = UpperBound(points, timestamp) - 1;
                    if (position < 0 || points[position].Timestamp != timestamp) return;
                }
                int first = function == SampleFunction.Instant ? end - 1 : UpperBound(points, start);
                EvaluatePoints(key, points, first, end);
            });

            if (merged.Count == 0) return result;
            _metricsStore.VisitSeries((_, deployment, pod, rawKey, points) =>
            {
                if (!TrySelect(selector, deployment, pod, rawKey, out var key) ||
                    !merged.TryGetValue(key, out var selected)) return;
                int end = UpperBound(points, Now);
                for (int i = UpperBound(points, start); i < end; i++)
                {
                    var point = points[i];
                    selected[point.Timestamp] = point;
                }
            });
        }

        foreach (var (key, points) in merged)
        {
            if (points.Count == 0 || (_requiredTimestamp is long timestamp && !points.ContainsKey(timestamp))) continue;
            EvaluatePoints(key, points.Values.AsValueEnumerable().ToArray(), 0, points.Count);
        }
        return result;

        void EvaluatePoints(SeriesKey key, IReadOnlyList<MetricPoint> points, int first, int end)
        {
            if (PromQlRangeFunctions.TryEvaluate(function, points, first, end, Now, window, out var value))
                result[key] = value;
        }
    }

    private bool TrySelect(MetricSelector selector, string deployment, string pod, string rawKey, out SeriesKey key)
    {
        key = default;
        return IsDeploymentInScope(deployment, selector.MetricName) &&
            selector.TrySelect(rawKey, deployment, pod, out key) &&
            (_externalSource is not null || _deployment is null || deployment == _deployment ||
             key.Get("function") == _deployment);
    }

    // InMemoryMetricsStore maintains timestamp order even for late observations.
    private static int UpperBound(IReadOnlyList<MetricPoint> points, long timestamp)
    {
        int low = 0;
        int high = points.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (points[mid].Timestamp <= timestamp) low = mid + 1;
            else high = mid;
        }
        return low;
    }

    // Queue gauges are emitted by SlimFaas, not by the function's HTTP server.
    // Only these built-in gauges may cross the deployment boundary, and only
    // for the function being evaluated. Application metrics remain isolated.
    private bool IsDeploymentInScope(string source, string metricName)
        => _externalSource is not null
            ? string.Equals(source, _externalSource, StringComparison.Ordinal)
            : !source.StartsWith(ExternalMetricsSource.Prefix, StringComparison.Ordinal) &&
              (_deployment is null ||
           string.Equals(source, _deployment, StringComparison.Ordinal) ||
           (string.Equals(source, "slimfaas", StringComparison.Ordinal) && metricName is
               "slimfaas_function_queue_ready_items" or
               "slimfaas_function_queue_in_flight_items" or
               "slimfaas_function_queue_retry_pending_items"));

    private static long ToLookbackSeconds(TimeSpan lookback)
        => Math.Max(1L, (long)Math.Ceiling(lookback.TotalSeconds));
}
