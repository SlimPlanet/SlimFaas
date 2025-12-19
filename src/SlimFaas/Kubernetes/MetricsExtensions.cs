using System.Globalization;

namespace SlimFaas.Kubernetes;

public static class MetricsExtensions
{
    private const string ScrapeAnnotation = "prometheus.io/scrape";
    private const string SchemeAnnotation = "prometheus.io/scheme";
    private const string PathAnnotation   = "prometheus.io/path";
    private const string PortAnnotation   = "prometheus.io/port";

    private const string DefaultScheme = "http";
    private const string DefaultPath   = "/metrics";
    private const string SlimFaasKey   = "slimfaas";

    private static bool IsScrapeEnabled(IDictionary<string, string>? annotations)
    {
        if (annotations is null)
            return false;

        if (!annotations.TryGetValue(ScrapeAnnotation, out var raw) || string.IsNullOrWhiteSpace(raw))
            return false;

        var v = raw.Trim();
        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
               || v.Equals("1", StringComparison.OrdinalIgnoreCase)
               || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveScheme(IDictionary<string, string>? annotations)
    {
        if (annotations is null)
            return DefaultScheme;

        return annotations.TryGetValue(SchemeAnnotation, out var s) && !string.IsNullOrWhiteSpace(s)
            ? s.Trim()
            : DefaultScheme;
    }

    private static string ResolvePath(IDictionary<string, string>? annotations)
    {
        var raw = annotations is not null && annotations.TryGetValue(PathAnnotation, out var p)
            ? p
            : DefaultPath;

        if (string.IsNullOrWhiteSpace(raw))
            raw = DefaultPath;

        raw = raw.Trim();

        return raw.StartsWith("/", StringComparison.Ordinal)
            ? raw
            : "/" + raw;
    }

    private static int? ResolvePort(IDictionary<string, string>? annotations)
    {
        if (annotations is null)
            return null;

        if (!annotations.TryGetValue(PortAnnotation, out var raw) || string.IsNullOrWhiteSpace(raw))
            return null;

        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port > 0
            ? port
            : null;
    }

    // Pod -> liste d’URLs /metrics (0 ou 1 URL selon annotations)
    public static IList<string> GetMetricsTargets(this PodInformation pod)
    {
        if (string.IsNullOrWhiteSpace(pod.Ip) || pod.Ready == false)
            return new List<string>();

        var annotations = pod.Annotations;

        // STRICT : nécessite scrape=true ET port annoté
        if (!IsScrapeEnabled(annotations))
            return new List<string>();

        var port = ResolvePort(annotations);
        if (port is null)
            return new List<string>();

        var scheme = ResolveScheme(annotations);
        var path = ResolvePath(annotations);

        return new List<string> { $"{scheme}://{pod.Ip}:{port}{path}" };
    }

    private static IList<string> GetMetricsTargetsForPods(IEnumerable<PodInformation> pods)
        => pods
            .SelectMany(p => p.GetMetricsTargets())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    // Map global simple : 1 clé par déploiement + "slimfaas"
    public static IDictionary<string, IList<string>> GetMetricsTargets(this DeploymentsInformations infos)
    {
        var result = new Dictionary<string, IList<string>>(StringComparer.Ordinal);

        foreach (var fn in infos.Functions)
        {
            var targets = GetMetricsTargetsForPods(fn.Pods);
            if (targets.Count > 0)
                result[fn.Deployment] = targets;
        }

        var slimFaasTargets = GetMetricsTargetsForPods(infos.SlimFaas.Pods);
        if (slimFaasTargets.Count > 0)
            result[SlimFaasKey] = slimFaasTargets;

        return result;
    }
}
