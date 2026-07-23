using System.Diagnostics;
using System.Security.Cryptography;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Workers;

public class MetricsScrapingWorker(
    IReplicasService replicasService,
    IMasterService masterService,
    IHttpClientFactory httpClientFactory,
    IMetricsStore metricsStore,
    IDatabaseService databaseService,
    ISlimDataStatus slimDataStatus,
    IMetricsScrapingGuard scrapingGuard,
    IRequestedMetricsRegistry requestedMetricsRegistry,
    ILogger<MetricsScrapingWorker> logger,
    IOptions<SlimFaasOptions> slimFaasOptions,
    int delay = 0)
    : BackgroundService
{
    private const string MetricsStoreKey = "metrics:store";
    private const string MetricsStoreVersionKey = "metrics:store:version";
    private const long ThirtyMinutesInMilliseconds = 1_800_000;
    private static readonly TimeSpan PersistenceInterval = TimeSpan.FromSeconds(30);

    private readonly MetricsScrapingOptions _metricsScrapingOptions = slimFaasOptions.Value.MetricsScraping;
    private readonly int _scrapeIntervalMilliseconds = delay > 0
        ? delay
        : slimFaasOptions.Value.MetricsScraping.ScrapeIntervalMilliseconds;
    private DateTimeOffset _nextPersistenceUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLegacyHydrationUtc = DateTimeOffset.MinValue;
    private byte[]? _lastHydratedVersion;
    private byte[]? _lastLegacyPayloadHash;
    private bool _wasMaster;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cycleStartedTimestamp = Stopwatch.GetTimestamp();
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
                    await DelayUntilNextScrapeCycleAsync(cycleStartedTimestamp, stoppingToken);
                    continue;
                }

                if (!masterService.IsMaster)
                {
                    _wasMaster = false;
                    await TryHydrateMetricsFromDatabaseAsync(stoppingToken);
                    await Task.Delay(1000, stoppingToken);
                    continue;
                }

                if (!_wasMaster)
                {
                    // Restore the latest persisted history before the first scrape
                    // after startup or a leadership change. This keeps range queries
                    // immediately usable without putting persistence on the hot path.
                    await TryHydrateMetricsFromDatabaseAsync(stoppingToken);
                    _wasMaster = true;
                }

                if (!masterService.IsMaster)
                {
                    _wasMaster = false;
                    continue;
                }

                var targetsByDeployment = deployments.GetMetricsTargets();

                // Si aucune cible annotée prometheus n'existe, on ne fait rien
                if (targetsByDeployment.Count == 0)
                {
                    await DelayUntilNextScrapeCycleAsync(cycleStartedTimestamp, stoppingToken);
                    continue;
                }

                var requestedMetricNames = requestedMetricsRegistry.GetRequestedMetricNames();
                if (requestedMetricNames.Count == 0)
                {
                    await DelayUntilNextScrapeCycleAsync(cycleStartedTimestamp, stoppingToken);
                    continue;
                }

                var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                foreach (var (deployment, urls) in targetsByDeployment)
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation(
                            "Scraping metrics for deployment {Deployment} with {TargetCount} targets",
                            deployment,
                            urls.Count);
                    }

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
                await PersistMetricsSnapshotIfDueAsync(stoppingToken);
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
                await DelayUntilNextScrapeCycleAsync(cycleStartedTimestamp, stoppingToken);
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

    private async Task DelayUntilNextScrapeCycleAsync(
        long cycleStartedTimestamp,
        CancellationToken stoppingToken)
    {
        var elapsed = Stopwatch.GetElapsedTime(cycleStartedTimestamp);
        var remaining = TimeSpan.FromMilliseconds(_scrapeIntervalMilliseconds) - elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, stoppingToken);
    }

    private async Task PersistMetricsSnapshotIfDueAsync(CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextPersistenceUtc)
            return;

        try
        {
            var record = metricsStore.CreateRecord();
            if (record.Store.Count == 0)
                return;

            var bytes = MemoryPackSerializer.Serialize(record);
            var version = Guid.NewGuid().ToByteArray();

            // Publish data first and the small version marker last. Followers only
            // read the large payload after observing the new marker.
            await databaseService.SetAsync(MetricsStoreKey, bytes, ThirtyMinutesInMilliseconds);
            await databaseService.SetAsync(
                MetricsStoreVersionKey,
                version,
                ThirtyMinutesInMilliseconds);

            _lastHydratedVersion = version.ToArray();
            _lastLegacyPayloadHash = null;
            _nextPersistenceUtc = now + PersistenceInterval;
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
            var version = await databaseService.GetAsync(MetricsStoreVersionKey);
            if (version is { Length: > 0 })
            {
                if (_lastHydratedVersion is not null &&
                    version.AsSpan().SequenceEqual(_lastHydratedVersion))
                {
                    return;
                }
            }
            else
            {
                // Compatibility with a scraper from an older rolling deployment.
                // The old writer has no version key, so inspect the legacy payload
                // at the new 30-second persistence cadence and synthesize a version
                // from its hash.
                var now = DateTimeOffset.UtcNow;
                if (now < _nextLegacyHydrationUtc)
                    return;
                _nextLegacyHydrationUtc = now + PersistenceInterval;
            }

            var bytes = await databaseService.GetAsync(MetricsStoreKey);
            if (bytes is null || bytes.Length == 0)
                return;

            byte[]? legacyPayloadHash = null;
            if (version is not { Length: > 0 })
            {
                legacyPayloadHash = SHA256.HashData(bytes);
                if (_lastLegacyPayloadHash is not null &&
                    legacyPayloadHash.AsSpan().SequenceEqual(_lastLegacyPayloadHash))
                {
                    return;
                }
            }

            var record = MemoryPackSerializer.Deserialize<MetricsStoreRecord>(bytes);
            if (record is null)
                return;

            metricsStore.ReplaceFromRecord(record);
            if (version is { Length: > 0 })
            {
                _lastHydratedVersion = version.ToArray();
                _lastLegacyPayloadHash = null;
            }
            else
            {
                _lastLegacyPayloadHash = legacyPayloadHash;
            }
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
