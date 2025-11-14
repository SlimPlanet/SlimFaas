using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.RegularExpressions;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IMetricsStore
{
    void Add(long timestamp, string deployment, string podIp, IReadOnlyDictionary<string, double> metrics);
}

public class InMemoryMetricsStore : IMetricsStore
{
    private readonly ConcurrentDictionary<long,
        ConcurrentDictionary<string,
            ConcurrentDictionary<string,
                ConcurrentDictionary<string, double>>>> _store = new();

    public void Add(long timestamp, string deployment, string podIp, IReadOnlyDictionary<string, double> metrics)
    {
        var d = _store.GetOrAdd(timestamp, _ => new());
        var dd = d.GetOrAdd(deployment, _ => new());
        var p = dd.GetOrAdd(podIp, _ => new());
        foreach (var kv in metrics)
            p[kv.Key] = kv.Value;
    }

    public IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Snapshot()
    {
        return _store.ToDictionary(
            t => t.Key,
            t => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>)t.Value.ToDictionary(
                d => d.Key,
                d => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>)d.Value.ToDictionary(
                    p => p.Key,
                    p => (IReadOnlyDictionary<string, double>)p.Value.ToDictionary(m => m.Key, m => m.Value)
                )
            )
        );
    }
}

public class MetricsScrapingWorker(
    IReplicasService replicasService,
    IRaftCluster cluster,
    IHttpClientFactory httpClientFactory,
    IMetricsStore metricsStore,
    ISlimDataStatus slimDataStatus,
    ILogger<MetricsScrapingWorker> logger,
    int delay = 5_000)
    : BackgroundService
{
// Remplace TOUTE la déclaration existante de MetricLine par celle-ci :
    private static readonly Regex MetricLine = new(
        // <metric_name><optional {labels}> <value> [optional_timestamp]
        @"^\s*([a-zA-Z_:][a-zA-Z0-9_:]*)(\{[^}]*\})?\s+([-+]?(?:NaN|(?:\+|-)?Inf|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?))(?:\s+\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slimDataStatus.WaitForReadyAsync();
                if (!IsDesignatedScraperNode(replicasService, cluster, logger))
                {
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                var infos = replicasService.Deployments;
                var targetsByDeployment = infos.GetMetricsTargets();
                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var (deployment, urls) in targetsByDeployment)
                {
                    foreach (var url in urls)
                    {
                        try
                        {
                            var podIp = GetHostFromUrl(url);
                            if (string.IsNullOrEmpty(podIp))
                                continue;

                            var http = httpClientFactory.CreateClient(nameof(MetricsScrapingWorker));
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stoppingToken);
                            if (!resp.IsSuccessStatusCode)
                                continue;

                            var body = await resp.Content.ReadAsStringAsync(stoppingToken);
                            var parsed = ParsePrometheusText(body);
                            if (parsed.Count > 0)
                                metricsStore.Add(ts, deployment, podIp, parsed);
                        }
                        catch (Exception e)
                        {
                            logger.LogWarning(e, "metrics scrape error for {Url}", url);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global error in MetricsScrapingWorker");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch { }
        }
    }

    private static string? GetHostFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.Host;
        return null;
    }

    private static IReadOnlyDictionary<string, double> ParsePrometheusText(string body)
    {
        var dict = new Dictionary<string, double>(StringComparer.Ordinal);
        using var sr = new StringReader(body);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var m = MetricLine.Match(line);
            if (!m.Success)
                continue;

            var name = m.Groups[1].Value;
            var labels = m.Groups[2].Success ? m.Groups[2].Value : string.Empty; // ex: {label="a"}
            var key = string.Concat(name, labels).Trim();
            var valStr = m.Groups[3].Value.Trim();

            if (string.Equals(valStr, "NaN", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(valStr, "Inf", StringComparison.OrdinalIgnoreCase) || string.Equals(valStr, "+Inf", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(valStr, "-Inf", StringComparison.OrdinalIgnoreCase))
                continue;

            if (double.TryParse(valStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                dict[key] = value;
        }
        return dict;
    }

    private static bool IsDesignatedScraperNode(IReplicasService replicasService, IRaftCluster cluster, ILogger logger)
    {
        var slimfaasPods = replicasService.Deployments.SlimFaas.Pods
            .Where(p => p.Started == true && !string.IsNullOrEmpty(p.Name))
            .ToList();
        if (slimfaasPods.Count == 0)
            return false;

        if(slimfaasPods.Count == 1)
            return true;

        var leaderEndpoint = cluster.Leader?.EndPoint?.ToString();
        string? leaderKey = null;
        if (!string.IsNullOrEmpty(leaderEndpoint))
        {
            leaderKey = SlimFaasPorts.RemoveLastPathSegment(leaderEndpoint!);
        }

        var ordered = slimfaasPods
            .Select(p => (pod: p, ordinal: TryParseOrdinal(p.Name)))
            .OrderBy(t => t.ordinal)
            .ToList();

        string? smallestNonLeaderPodName = null;
        foreach (var t in ordered)
        {
            var endpoint = SlimDataEndpoint.Get(t.pod);
            var key = SlimFaasPorts.RemoveLastPathSegment(endpoint);
            if (!string.Equals(key, leaderKey, StringComparison.Ordinal))
            {
                smallestNonLeaderPodName = t.pod.Name;
                break;
            }
        }
        if (smallestNonLeaderPodName is null)
            return false;

        var currentPodName = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(currentPodName))
            return false;

        var isDesignated = string.Equals(currentPodName, smallestNonLeaderPodName, StringComparison.Ordinal);
        return isDesignated;
    }

    private static int TryParseOrdinal(string podName)
    {
        var idx = podName.LastIndexOf('-');
        if (idx < 0 || idx == podName.Length - 1)
            return int.MaxValue;
        if (int.TryParse(podName.AsSpan(idx + 1), out var n))
            return n;
        return int.MaxValue;
    }
}
