using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SlimFaas.Kubernetes;

public interface IRequestedMetricsRegistry
{
    void RegisterFromQuery(string promql);
    bool IsRequestedKey(string metricKey);

    IReadOnlyCollection<string> GetRequestedMetricNames();
}

public sealed class RequestedMetricsRegistry : IRequestedMetricsRegistry
{
    private readonly ConcurrentDictionary<string, byte> _metricNames =
        new(StringComparer.Ordinal);

    // Très simple : on chope les noms de métrique avant un "{" ou "("
    private static readonly Regex MetricNameRegex = new(
        @"([a-zA-Z_:][a-zA-Z0-9_:]*)\s*(?=\{|\()",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public void RegisterFromQuery(string promql)
    {
        if (string.IsNullOrWhiteSpace(promql))
            return;

        foreach (Match m in MetricNameRegex.Matches(promql))
        {
            var name = m.Groups[1].Value;
            if (!string.IsNullOrEmpty(name))
            {
                _metricNames.TryAdd(name, 0);
            }
        }
    }

    public IReadOnlyCollection<string> GetRequestedMetricNames()
        => _metricNames.Keys.ToArray();

    public bool IsRequestedKey(string metricKey)
    {
        // metricKey = "http_server_requests_seconds_sum{...}"
        // On check juste si ça commence par un des noms enregistrés.
        foreach (var name in _metricNames.Keys)
        {
            if (metricKey.StartsWith(name, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
