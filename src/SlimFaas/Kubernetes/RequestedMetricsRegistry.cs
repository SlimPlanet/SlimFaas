using System.Collections.Concurrent;
using System.Text;
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

        // Fonctions / mots-clés PromQL à ignorer
        private static readonly HashSet<string> IgnoredIdentifiers = new(StringComparer.Ordinal)
        {
            // Fonctions d’agrégation
            "sum", "min", "max", "avg", "count", "stddev", "stdvar",
            "sum_over_time", "avg_over_time", "min_over_time", "max_over_time",
            "count_over_time", "stddev_over_time", "stdvar_over_time",
            "quantile", "quantile_over_time", "histogram_quantile",

            // Fonctions de taux / dérivées
            "rate", "irate", "increase",

            // Fonctions diverses
            "abs", "absent", "clamp_max", "clamp_min",
            "day_of_month", "day_of_week", "days_in_month",
            "hour", "minute", "month", "year", "timestamp",
            "sort", "sort_desc",

            // Matching / filtres
            "on", "ignoring", "group_left", "group_right",
            "without", "by", "bool", "and", "or", "unless", "offset"
        };

        private readonly ConcurrentDictionary<string, byte> _metrics =
            new(StringComparer.Ordinal);

        public void RegisterMetricName(string metricName)
        {
            if (string.IsNullOrWhiteSpace(metricName))
                return;

            _metrics.TryAdd(metricName, 0);
        }

        public void RegisterFromQuery(string promql)
        {
            if (string.IsNullOrWhiteSpace(promql))
                return;

            // 1. Nettoyage grossier de la requête :
            //    - supprime le contenu des {} (labels)
            //    - supprime le contenu des [] (range vector)
            //    - supprime le contenu des "..." (strings)
            var cleaned = StripLabelsRangesAndStrings(promql);

            // 2. Extraction des identifiants
            foreach (Match m in MetricNameRegex.Matches(cleaned))
            {
                var name = m.Value;

                // Ignore fonctions / mots-clés
                if (IgnoredIdentifiers.Contains(name))
                    continue;

                // Ignore métriques internes Prometheus (__name__, __meta_*, etc.)
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

        /// <summary>
        /// Supprime les portions de texte :
        /// - dans { ... } (labels)
        /// - dans [ ... ] (range vectors)
        /// - dans " ... " (strings)
        /// mais garde le reste inchangé.
        /// </summary>
        private static string StripLabelsRangesAndStrings(string promql)
        {
            var sb = new StringBuilder(promql.Length);
            bool inString = false;
            int braceDepth = 0;   // {}
            int bracketDepth = 0; // []

            for (int i = 0; i < promql.Length; i++)
            {
                char c = promql[i];

                // Si on est dans un string "..."
                if (inString)
                {
                    if (c == '"' && (i == 0 || promql[i - 1] != '\\'))
                    {
                        inString = false;
                    }
                    continue; // on skip le contenu du string
                }

                // Si on est dans un bloc {...}
                if (braceDepth > 0)
                {
                    if (c == '{') braceDepth++;
                    else if (c == '}') braceDepth--;
                    continue; // on skip les labels
                }

                // Si on est dans un bloc [...]
                if (bracketDepth > 0)
                {
                    if (c == ']') bracketDepth--;
                    continue; // on skip les ranges
                }

                // Début d'un string
                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                // Début d'un bloc labels
                if (c == '{')
                {
                    braceDepth = 1;
                    continue;
                }

                // Début d'un range [1m]
                if (c == '[')
                {
                    bracketDepth = 1;
                    continue;
                }

                // Sinon, on garde le caractère
                sb.Append(c);
            }

            return sb.ToString();
        }
    }

