using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Workers;



public class MetricsScrapingWorker(
    IReplicasService replicasService,
    IRaftCluster cluster,
    IHttpClientFactory httpClientFactory,
    IMetricsStore metricsStore,
    IDatabaseService databaseService,
    ISlimDataStatus slimDataStatus,
    IMetricsScrapingGuard scrapingGuard,
    IRequestedMetricsRegistry requestedMetricsRegistry,
    ILogger<MetricsScrapingWorker> logger,
    IOptions<SlimFaasOptions> slimFaasOptions,
    INamespaceProvider namespaceProvider,
    int delay = 5_000)
    : BackgroundService
{
    private readonly string _baseSlimDataUrl = slimFaasOptions.Value.BaseSlimDataUrl;
    private readonly string _namespace = namespaceProvider.CurrentNamespace;
    private readonly MetricsScrapingOptions _metricsScrapingOptions = slimFaasOptions.Value.MetricsScraping;

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

                if (!IsDesignatedScraperNode(replicasService, cluster, logger, _baseSlimDataUrl, _namespace))
                {
                    await TryHydrateMetricsFromDatabaseAsync(stoppingToken);
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                var targetsByDeployment = deployments.GetMetricsTargets();

                // Si aucune cible annotée prometheus n'existe, on ne fait rien
                if (targetsByDeployment.Count == 0)
                {
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                var requestedMetricNames = requestedMetricsRegistry.GetRequestedMetricNames();
                if (requestedMetricNames.Count == 0)
                {
                    await Task.Delay(delay, stoppingToken);
                    continue;
                }

                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var (deployment, urls) in targetsByDeployment)
                {
                    if(logger.IsEnabled(LogLevel.Information))
                        logger.LogInformation("Scraping metrics for deployment {0} with {1} targets", deployment, urls.Count);
                    foreach (var url in urls)
                    {
                        try
                        {
                            var podIp = GetHostFromUrl(url);
                            if (string.IsNullOrEmpty(podIp))
                                continue;

                            var http = httpClientFactory.CreateClient(nameof(MetricsScrapingWorker));
                            using var req = new HttpRequestMessage(HttpMethod.Get, url);
                            using var scrapeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            scrapeTimeout.CancelAfter(TimeSpan.FromSeconds(_metricsScrapingOptions.RequestTimeoutSeconds));
                            using var resp = await http.SendAsync(
                                req,
                                HttpCompletionOption.ResponseHeadersRead,
                                scrapeTimeout.Token);
                            if (!resp.IsSuccessStatusCode)
                                continue;

                            var contentLength = resp.Content.Headers.ContentLength;
                            if (contentLength > _metricsScrapingOptions.MaxResponseBytes)
                            {
                                logger.LogWarning(
                                    "Metrics scrape rejected for {Url}: Content-Length {ContentLength} exceeds " +
                                    "MaxResponseBytes {MaxResponseBytes}",
                                    url,
                                    contentLength,
                                    _metricsScrapingOptions.MaxResponseBytes);
                                continue;
                            }

                            await using var body = await resp.Content
                                .ReadAsStreamAsync(scrapeTimeout.Token)
                                .ConfigureAwait(false);
                            var parsed = await PrometheusStreamParser.ParseAsync(
                                body,
                                requestedMetricNames,
                                _metricsScrapingOptions,
                                scrapeTimeout.Token);
                            if (parsed.Status != PrometheusStreamParseStatus.Success)
                            {
                                logger.LogWarning(
                                    "Metrics scrape rejected for {Url}: Reason={Reason}, BytesRead={BytesRead}, " +
                                    "LinesRead={LinesRead}",
                                    url,
                                    parsed.Status,
                                    parsed.BytesRead,
                                    parsed.LinesRead);
                                continue;
                            }

                            if (parsed.Metrics.Count > 0)
                                metricsStore.Add(ts, deployment, podIp, parsed.Metrics);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogWarning(
                                "Metrics scrape timed out after {TimeoutSeconds} seconds for {Url}",
                                _metricsScrapingOptions.RequestTimeoutSeconds,
                                url);
                        }
                        catch (Exception e)
                        {
                            logger.LogWarning(e, "metrics scrape error for {Url}", url);
                        }
                    }
                }
                await PersistMetricsSnapshotAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
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

    private static bool IsDesignatedScraperNode(IReplicasService replicasService, IRaftCluster cluster, ILogger logger, string baseSlimDataUrl, string namespace_)
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
            var endpoint = SlimDataEndpoint.Get(t.pod, baseSlimDataUrl, namespace_);
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

    const long ThirtyMinutesInMilliseconds = 1800000;

    private async Task PersistMetricsSnapshotAsync(CancellationToken stoppingToken)
    {
        try
        {
            var snapshot = metricsStore.Snapshot();
            if (snapshot.Count == 0)
                return;

            var record = MetricsStoreRecord.FromSnapshot(snapshot);
            var bytes = MemoryPackSerializer.Serialize(record);
            await databaseService.SetAsync("metrics:store", bytes, ThirtyMinutesInMilliseconds);
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
