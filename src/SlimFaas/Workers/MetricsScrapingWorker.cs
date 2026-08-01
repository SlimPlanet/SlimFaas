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
    private readonly bool _hasDelayOverride = delay > 0;
    private readonly int _scrapeIntervalMilliseconds = delay > 0
        ? delay
        : slimFaasOptions.Value.MetricsScraping.ScrapeIntervalMilliseconds;
    private readonly Dictionary<string, long> _nextScrapeByDeployment = new(StringComparer.Ordinal);
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
                var loopIntervalMilliseconds = ResolveLoopIntervalMilliseconds(deployments);

                // 👉 Est-ce qu'au moins une fonction utilise le ScaleConfig ?
                var scaledDeployments = deployments.Functions
                    .Where(f => f.Scale is { Triggers.Count: > 0 })
                    .Select(f => f.Deployment)
                    .ToHashSet(StringComparer.Ordinal);

                var hasScaleConfig = scaledDeployments.Count > 0;

                // 👉 Si aucune fonction n'a Scale ET aucune requête PromQL n'a été faite, on ne scrape pas
                if (!hasScaleConfig && !scrapingGuard.IsEnabled)
                {
                    await DelayUntilNextScrapeCycleAsync(
                        cycleStartedTimestamp,
                        loopIntervalMilliseconds,
                        stoppingToken);
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
                    await DelayUntilNextScrapeCycleAsync(
                        cycleStartedTimestamp,
                        loopIntervalMilliseconds,
                        stoppingToken);
                    continue;
                }

                var requestedMetricNames = requestedMetricsRegistry.GetRequestedMetricNames();
                if (requestedMetricNames.Count == 0)
                {
                    await DelayUntilNextScrapeCycleAsync(
                        cycleStartedTimestamp,
                        loopIntervalMilliseconds,
                        stoppingToken);
                    continue;
                }

                var dueTargets = SelectDueTargets(deployments, targetsByDeployment);
                if (dueTargets.Count > 0)
                {
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    using var concurrency = new SemaphoreSlim(_metricsScrapingOptions.MaxConcurrentTargets);
                    var scrapeTasks = dueTargets
                        .Select(target => ScrapeTargetBoundedAsync(
                            target.Deployment,
                            target.Url,
                            ts,
                            requestedMetricNames,
                            concurrency,
                            stoppingToken))
                        .ToArray();
                    await Task.WhenAll(scrapeTasks);
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
                var deployments = replicasService.Deployments;
                await DelayUntilNextScrapeCycleAsync(
                    cycleStartedTimestamp,
                    ResolveLoopIntervalMilliseconds(deployments),
                    stoppingToken);
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

    private static string? GetTargetIdentityFromUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            // Kubernetes targets have one IP per pod, so keeping the historic
            // host-only identity preserves persisted series during upgrades.
            // The local runner exposes every replica on the loopback address
            // with a distinct port; include that port to avoid merging pods.
            return u.IsLoopback ? u.Authority : u.Host;
        }
        return null;
    }

    private async Task DelayUntilNextScrapeCycleAsync(
        long cycleStartedTimestamp,
        int intervalMilliseconds,
        CancellationToken stoppingToken)
    {
        var elapsed = Stopwatch.GetElapsedTime(cycleStartedTimestamp);
        var remaining = TimeSpan.FromMilliseconds(intervalMilliseconds) - elapsed;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, stoppingToken);
    }

    private int ResolveLoopIntervalMilliseconds(DeploymentsInformations deployments)
    {
        if (_hasDelayOverride)
            return _scrapeIntervalMilliseconds;

        var minimum = _scrapeIntervalMilliseconds;
        foreach (var function in deployments.Functions)
        {
            if (function.Scale is { Triggers.Count: > 0, ScrapeIntervalMilliseconds: { } configured })
                minimum = Math.Min(minimum, configured);
        }

        return minimum;
    }

    private List<(string Deployment, string Url)> SelectDueTargets(
        DeploymentsInformations deployments,
        IDictionary<string, IList<string>> targetsByDeployment)
    {
        var functions = deployments.Functions.ToDictionary(
            static function => function.Deployment,
            StringComparer.Ordinal);
        var currentDeployments = targetsByDeployment.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var staleDeployment in _nextScrapeByDeployment.Keys
                     .Where(key => !currentDeployments.Contains(key))
                     .ToArray())
        {
            _nextScrapeByDeployment.Remove(staleDeployment);
        }

        var now = Stopwatch.GetTimestamp();
        var dueTargets = new List<(string Deployment, string Url)>();
        foreach (var (deployment, urls) in targetsByDeployment)
        {
            if (_nextScrapeByDeployment.TryGetValue(deployment, out var nextScrape) && now < nextScrape)
                continue;

            var intervalMilliseconds = _scrapeIntervalMilliseconds;
            if (!_hasDelayOverride &&
                functions.TryGetValue(deployment, out var function) &&
                function.Scale?.ScrapeIntervalMilliseconds is { } configured)
            {
                intervalMilliseconds = configured;
            }

            _nextScrapeByDeployment[deployment] = now +
                (long)(intervalMilliseconds / 1_000.0 * Stopwatch.Frequency);
            dueTargets.AddRange(urls.Select(url => (deployment, url)));

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Scraping metrics for deployment {Deployment} with {TargetCount} targets",
                    deployment,
                    urls.Count);
            }
        }

        return dueTargets;
    }

    private async Task ScrapeTargetBoundedAsync(
        string deployment,
        string url,
        long timestamp,
        IReadOnlyCollection<string> requestedMetricNames,
        SemaphoreSlim concurrency,
        CancellationToken stoppingToken)
    {
        await concurrency.WaitAsync(stoppingToken);
        try
        {
            await ScrapeTargetAsync(deployment, url, timestamp, requestedMetricNames, stoppingToken);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task ScrapeTargetAsync(
        string deployment,
        string url,
        long timestamp,
        IReadOnlyCollection<string> requestedMetricNames,
        CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var targetIdentity = GetTargetIdentityFromUrl(url);
            if (string.IsNullOrEmpty(targetIdentity))
            {
                MetricsScrapingTelemetry.RecordFailure(deployment, "invalid_url");
                return;
            }

            var http = httpClientFactory.CreateClient(nameof(MetricsScrapingWorker));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var scrapeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            scrapeTimeout.CancelAfter(TimeSpan.FromSeconds(_metricsScrapingOptions.RequestTimeoutSeconds));
            using var resp = await http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                scrapeTimeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                MetricsScrapingTelemetry.RecordFailure(deployment, "http_status");
                return;
            }

            var contentLength = resp.Content.Headers.ContentLength;
            if (contentLength > _metricsScrapingOptions.MaxResponseBytes)
            {
                logger.LogWarning(
                    "Metrics scrape rejected for {Url}: Content-Length {ContentLength} exceeds " +
                    "MaxResponseBytes {MaxResponseBytes}",
                    url,
                    contentLength,
                    _metricsScrapingOptions.MaxResponseBytes);
                MetricsScrapingTelemetry.RecordFailure(deployment, "response_too_large");
                return;
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
                MetricsScrapingTelemetry.RecordFailure(deployment, "parse");
                return;
            }

            if (parsed.Metrics.Count > 0)
                metricsStore.Add(timestamp, deployment, targetIdentity, parsed.Metrics);
            MetricsScrapingTelemetry.RecordSuccess(deployment);
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
            MetricsScrapingTelemetry.RecordFailure(deployment, "timeout");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "metrics scrape error for {Url}", url);
            MetricsScrapingTelemetry.RecordFailure(deployment, "exception");
        }
        finally
        {
            MetricsScrapingTelemetry.RecordDuration(deployment, Stopwatch.GetElapsedTime(started));
        }
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
