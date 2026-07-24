using System.Text;
using System.Text.RegularExpressions;
using SlimFaas.Workers;

namespace SlimFaas.Kubernetes;

internal sealed class MetricSelector
{
    public string MetricName { get; }
    public List<(string label, string? equal, Regex? regex)> Matchers { get; } = [];

    public MetricSelector(string name) => MetricName = name;

    public bool Match(string name, Dictionary<string, string> labels)
    {
        if (!string.Equals(name, MetricName, StringComparison.Ordinal))
            return false;

        foreach (var (label, equal, regex) in Matchers)
        {
            if (!labels.TryGetValue(label, out var v))
                return false;

            if (regex is not null)
            {
                if (!regex.IsMatch(v)) return false;
            }
            else if (equal is not null)
            {
                if (!string.Equals(v, equal, StringComparison.Ordinal)) return false;
            }
        }

        return true;
    }
}

internal sealed class EvalContext
{
    private readonly IReadOnlyDictionary<long,
        IReadOnlyDictionary<string,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string, double>>>>? _snapshot;
    private readonly IMetricsStore? _metricsStore;
    private readonly long _instantSelectorLookbackSeconds;

    public readonly long Now;

    public EvalContext(
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> store,
        long now,
        TimeSpan instantSelectorLookback)
    {
        _snapshot = store;
        Now = now;
        _instantSelectorLookbackSeconds = ToLookbackSeconds(instantSelectorLookback);
    }

    public EvalContext(
        IMetricsStore metricsStore,
        long now,
        TimeSpan instantSelectorLookback)
    {
        _metricsStore = metricsStore;
        Now = now;
        _instantSelectorLookbackSeconds = ToLookbackSeconds(instantSelectorLookback);
    }

    // Returns all series (seriesKey → SortedList<timestamp, value>) matching the selector.
    public Dictionary<string, SortedList<long, double>> SelectSeries(MetricSelector selector, TimeSpan? window = null)
    {
        var isInstantSelector = !window.HasValue;
        var lookbackSeconds = isInstantSelector
            ? _instantSelectorLookbackSeconds
            : (long)Math.Ceiling(window!.Value.TotalSeconds);
        var fromTs = Now - lookbackSeconds;

        var series = new Dictionary<string, SortedList<long, double>>(StringComparer.Ordinal);

        if (_metricsStore is not null)
        {
            _metricsStore.VisitSeries((_, _, podIp, metricKey, points) =>
            {
                if (!TryParseMetricKey(metricKey, out var name, out var labels) ||
                    !selector.Match(name, labels))
                {
                    return;
                }

                SortedList<long, double>? selectedPoints = null;
                for (var index = 0; index < points.Count; index++)
                {
                    var point = points[index];
                    if (IsOutsideSelection(point.Timestamp, fromTs, isInstantSelector))
                        continue;

                    selectedPoints ??= new SortedList<long, double>(points.Count - index);
                    selectedPoints[point.Timestamp] = point.Value;
                }

                if (selectedPoints is null)
                    return;

                var baseKey = BuildSeriesKey(name, labels);
                series[$"{baseKey}|pod={podIp}"] = selectedPoints;
            });
            return series;
        }

        foreach (var (ts, depMap) in _snapshot!)
        {
            if (IsOutsideSelection(ts, fromTs, isInstantSelector)) continue;

            foreach (var (_, podMap) in depMap)
            {
                foreach (var (podIp, metrics) in podMap)
                {
                    foreach (var (metricKey, value) in metrics)
                    {
                        if (!TryParseMetricKey(metricKey, out var name, out var labels))
                            continue;

                        if (!selector.Match(name, labels))
                            continue;

                        var baseKey = BuildSeriesKey(name, labels);
                        var seriesKey = $"{baseKey}|pod={podIp}";

                        if (!series.TryGetValue(seriesKey, out var list))
                        {
                            list = new SortedList<long, double>();
                            series[seriesKey] = list;
                        }

                        // Keep last value when multiple entries share the same timestamp.
                        if (!list.ContainsKey(ts))
                            list.Add(ts, value);
                        else
                            list[ts] = value;
                    }
                }
            }
        }

        return series;
    }

    private static long ToLookbackSeconds(TimeSpan lookback)
        => Math.Max(1L, (long)Math.Ceiling(lookback.TotalSeconds));

    private static bool IsOutsideSelection(long timestamp, long fromTs, bool isInstantSelector)
        => isInstantSelector ? timestamp <= fromTs : timestamp < fromTs;

    internal static bool TryParseMetricKey(string key, out string name, out Dictionary<string, string> labels)
    {
        name = key;
        labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = key.IndexOf('{');
        if (i < 0) return true;

        var j = key.LastIndexOf('}');
        if (j < i) return false;

        name = key[..i];
        var content = key.Substring(i + 1, j - i - 1).Trim();
        if (string.IsNullOrEmpty(content)) return true;

        foreach (var pair in SplitLabels(content))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var k = pair[..eq].Trim();
            var v = pair[(eq + 1)..].Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                v = v.Substring(1, v.Length - 2);
            labels[k] = v;
        }
        return true;

        static IEnumerable<string> SplitLabels(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (c == ',')
                {
                    yield return sb.ToString().Trim();
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) yield return sb.ToString().Trim();
        }
    }

    internal static string BuildSeriesKey(string name, Dictionary<string, string> labels)
    {
        if (labels.Count == 0) return name;
        var sb = new StringBuilder();
        sb.Append(name);
        sb.Append('{');
        bool first = true;
        foreach (var kv in labels.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(kv.Key);
            sb.Append("=\"");
            sb.Append(kv.Value.Replace("\"", "\\\""));
            sb.Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }
}
