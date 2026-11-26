using System.Collections.Concurrent;

namespace SlimFaas.Kubernetes;

public interface IRequestedMetricsRegistry
{
    void RegisterFromQuery(string promql);
    bool IsRequestedKey(string metricKey);

    IReadOnlyCollection<string> GetRequestedMetricNames();
}

public sealed class RequestedMetricsRegistry : IRequestedMetricsRegistry
{
    private readonly ConcurrentDictionary<string, byte> _metrics =
        new(StringComparer.Ordinal);

    public void RegisterMetricName(string metricName)
    {
        if (string.IsNullOrWhiteSpace(metricName))
            return;

        _metrics.TryAdd(metricName, 0);
    }

    // Already-parsed queries: registration is purely additive, so re-parsing an
    // identical query is always a no-op. Bounded in practice by the number of
    // distinct configured trigger queries.
    private readonly ConcurrentDictionary<string, byte> _processedQueries =
        new(StringComparer.Ordinal);

    public void RegisterFromQuery(string promql)
    {
        if (string.IsNullOrWhiteSpace(promql))
            return;

        if (_processedQueries.ContainsKey(promql)) return;
        try
        {
            foreach (var name in PromQlQueryCompiler.Compile(promql).ReferencedMetricNames)
                RegisterMetricName(name);
            _processedQueries.TryAdd(promql, 0);
        }
        catch (FormatException)
        {
            // Query evaluation reports the error. An invalid query requests no samples.
        }
    }

    public IReadOnlyCollection<string> GetRequestedMetricNames()
        => _metrics.Keys.ToArray();

    public bool IsRequestedKey(string metricKey)
    {
        if (string.IsNullOrWhiteSpace(metricKey))
            return false;

        // metricKey peut être "metric_name{label="x"}"
        var braceIndex = metricKey.IndexOf('{', StringComparison.Ordinal);
        var name = braceIndex < 0 ? metricKey : metricKey[..braceIndex];

        return _metrics.ContainsKey(name);
    }

}
