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
        // Match tous les identifiants PromQL
        private static readonly Regex MetricNameRegex = new(
            @"[a-zA-Z_:][a-zA-Z0-9_:]*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Fonctions / mots-clés PromQL à ignorer (on peut l’enrichir au besoin)
        private static readonly HashSet<string> IgnoredIdentifiers = new(StringComparer.Ordinal)
        {
            // Fonctions d’agrégation
            "sum", "min", "max", "avg", "count", "stddev", "stdvar",
            "sum_over_time", "avg_over_time", "min_over_time", "max_over_time",
            "count_over_time", "stddev_over_time", "stdvar_over_time",
            "quantile", "quantile_over_time", "histogram_quantile",

            // Fonctions de taux / dérivées
            "rate", "irate", "increase",

            // Fonctions de manipulation de labels
            "label_replace", "label_join",

            // Fonctions diverses
            "abs", "absent", "clamp_max", "clamp_min",
            "day_of_month", "day_of_week", "days_in_month",
            "hour", "minute", "month", "year", "timestamp",
            "sort", "sort_desc",

            // Mots-clés de matching / filtres
            "on", "ignoring", "group_left", "group_right",
            "without", "by", "bool", "and", "or", "unless",
            "offset"
        };

        private readonly ConcurrentDictionary<string, byte> _metrics =
            new(StringComparer.Ordinal);

        public void RegisterMetricName(string metricName)
        {
            if (string.IsNullOrWhiteSpace(metricName))
                return;

            // On pourrait filtrer ici d’autres patterns si besoin
            _metrics.TryAdd(metricName, 0);
        }

        public void RegisterFromQuery(string promql)
        {
            if (string.IsNullOrWhiteSpace(promql))
                return;

            foreach (Match m in MetricNameRegex.Matches(promql))
            {
                var name = m.Value;

                // Ignore les fonctions / mots-clés
                if (IgnoredIdentifiers.Contains(name))
                    continue;

                // Ignore les métriques internes Prometheus (__name__, __meta_*, etc.)
                if (name.StartsWith("__", StringComparison.Ordinal))
                    continue;

                RegisterMetricName(name);
            }
        }

        public IReadOnlyCollection<string> GetRequestedMetricNames()
            => _metrics.Keys.ToArray();

        public bool IsRequestedKey(string metricKey)
        {
            if (string.IsNullOrWhiteSpace(metricKey))
                return false;

            // metricKey peut être "metric_name{label="x"}"
            var braceIndex = metricKey.IndexOf('{');
            var name = braceIndex < 0 ? metricKey : metricKey[..braceIndex];

            return _metrics.ContainsKey(name);
        }
    }

