// Ajoutez/Remplacez la classe utilitaire par ceci (même namespace SlimFaas.Kubernetes)

namespace SlimFaas.Kubernetes;

public static class MetricsExtensions
{
    private static bool IsScrapeEnabled(IDictionary<string, string>? ann)
    {
        if (ann is null) return false;
        if (!ann.TryGetValue("prometheus.io/scrape", out var v)) return false;
        v = v?.Trim()?.ToLowerInvariant() ?? "";
        return v is "true" or "1" or "yes";
    }

    private static string ResolveScheme(IDictionary<string, string>? ann)
    {
        // Pas de “fallback” vers d’autres sources; juste une valeur par défaut de protocole
        // (Prometheus considère http par défaut si scheme absent).
        if (ann is null) return "http";
        return ann.TryGetValue("prometheus.io/scheme", out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : "http";
    }

    private static string ResolvePath(IDictionary<string, string>? ann)
    {
        // Valeur par défaut usuelle côté Prometheus si non précisée.
        var p = (ann != null && ann.TryGetValue("prometheus.io/path", out var path)) ? path?.Trim() : "/metrics";
        if (string.IsNullOrWhiteSpace(p)) p = "/metrics";
        if (!p.StartsWith("/")) p = "/" + p;
        return p;
    }

    private static int? GetAnnotatedPort(IDictionary<string, string>? ann)
    {
        if (ann is null) return null;
        if (!ann.TryGetValue("prometheus.io/port", out var sp)) return null;
        return int.TryParse(sp?.Trim(), out var port) && port > 0 ? port : null;
    }

    // — Pod -> liste d’URLs /metrics (0 ou 1 URL selon annotations) —
    public static IList<string> GetMetricsTargets(this PodInformation pod)
    {
        if (string.IsNullOrWhiteSpace(pod.Ip)) return new List<string>();
        var ann = pod.Annotations;

        // STRICT : nécessite scrape=true ET port annoté. Sinon => aucune URL.
        if (!IsScrapeEnabled(ann)) return new List<string>();

        var port = GetAnnotatedPort(ann);
        if (port is null) return new List<string>();

        var scheme = ResolveScheme(ann);
        var path = ResolvePath(ann);

        return new List<string> { $"{scheme}://{pod.Ip}:{port}{path}" };
    }

    // — Map global : aucun regroupement “other” spéculatif, aucune déduction —
    public static IDictionary<string, IList<string>> GetMetricsTargets(this DeploymentsInformations infos)
    {
        var map = new Dictionary<string, IList<string>>();

        foreach (var fn in infos.Functions)
            map[fn.Deployment] = fn.Pods.SelectMany(p => p.GetMetricsTargets()).Distinct().ToList();

        map["slimfaas"] = infos.SlimFaas.Pods.SelectMany(p => p.GetMetricsTargets()).Distinct().ToList();

        return map;
    }
}
