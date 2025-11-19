using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas.Workers;



public class MetricsScrapingWorker(
    IReplicasService replicasService,
    IRaftCluster cluster,
    IHttpClientFactory httpClientFactory,
    IMetricsStore metricsStore,
    IDatabaseService databaseService,
    ISlimDataStatus slimDataStatus,
    IMetricsScrapingGuard scrapingGuard,
    ILogger<MetricsScrapingWorker> logger,
    int delay = 5_000)
    : BackgroundService
{
// Remplace TOUTE la déclaration existante de MetricLine par celle-ci :
    private static readonly Regex MetricLine = new(
        // <metric_name><optional {labels}> <value> [optional_timestamp]
        @"^\s*([a-zA-Z_:][a-zA-Z0-9_:]*)(\{[^}]*\})?\s+([-+]?(?:NaN|(?:\+|-)?Inf|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?))(?:\s+\d+)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(120));


    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await slimDataStatus.WaitForReadyAsync();

                var deployments = replicasService.Deployments;

                // 👉 Est-ce qu'au moins une fonction utilise le ScaleConfig ?
                var scaledDeployments = deployments.Functions
                    .Where(f => f.Scale is { Triggers.Count: > 0 })
                    .Select(f => f.Deployment)
                    .ToHashSet(StringComparer.Ordinal);

                var hasScaleConfig = scaledDeployments.Count > 0;

                // 👉 Si aucune fonction n'a Scale ET aucune requête PromQL n'a été faite, on ne scrape pas
                if (!hasScaleConfig && !scrapingGuard.IsEnabled)
                {
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                if (!IsDesignatedScraperNode(replicasService, cluster, logger))
                {
                    await TryHydrateMetricsFromDatabaseAsync(stoppingToken);
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                var targetsByDeployment = deployments.GetMetricsTargets();

                // 👉 Si on a des fonctions avec Scale, on ne scrape que celles-là
                if (hasScaleConfig)
                {
                    targetsByDeployment = targetsByDeployment
                        .Where(kvp => scaledDeployments.Contains(kvp.Key))
                        .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    if (targetsByDeployment.Count == 0)
                    {
                        await Task.Delay(delay, stoppingToken);
                        continue;
                    }
                }

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
                await PersistMetricsSnapshotAsync(stoppingToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Global error in MetricsScrapingWorker");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stoppingToken is cancelled; ignore.
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Unexpected error during delay in MetricsScrapingWorker");
            }
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

    private async Task PersistMetricsSnapshotAsync(CancellationToken stoppingToken)
    {
        try
        {
            var snapshot = metricsStore.Snapshot();
            if (snapshot.Count == 0)
                return;

            var record = MetricsStoreRecord.FromSnapshot(snapshot);
            var bytes = MemoryPackSerializer.Serialize(record);
            await databaseService.SetAsync("metrics:store", bytes);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unable to persist metrics store to database");
        }
    }

    private async Task TryHydrateMetricsFromDatabaseAsync(CancellationToken stoppingToken)
    {
        try
        {
            var bytes = await databaseService.GetAsync("metrics:store");
            if (bytes is null || bytes.Length == 0)
                return;

            var record = MemoryPackSerializer.Deserialize<MetricsStoreRecord>(bytes);
            if (record is null)
                return;

            metricsStore.ReplaceFromRecord(record);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unable to hydrate metrics store from database");
        }
    }

}
