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
    int delay = 0,
    IExternalMetricsSourceHealthRegistry? externalHealthRegistry = null)
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
    private readonly IExternalMetricsSourceHealthRegistry _externalHealthRegistry =
        externalHealthRegistry ?? new ExternalMetricsSourceHealthRegistry();
    private readonly Dictionary<string, long> _nextScrapeByScope = new(StringComparer.Ordinal);
    private DateTimeOffset _nextPersistenceUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _nextLegacyHydrationUtc = DateTimeOffset.MinValue;
    private byte[]? _lastHydratedVersion;
    private byte[]? _lastLegacyPayloadHash;
    private bool _wasMaster;

    private sealed record ScrapeTarget(
        string Scope,
        string Function,
        string Source,
        string Url,
        int IntervalMilliseconds,
        bool IsExternal);

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

                var targets = GetMetricsTargets(deployments);

                // Si aucune cible annotée prometheus n'existe, on ne fait rien
                if (targets.Count == 0)
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

                var dueTargets = SelectDueTargets(targets);
                if (dueTargets.Count > 0)
                {
                    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    using var concurrency = new SemaphoreSlim(_metricsScrapingOptions.MaxConcurrentTargets);
                    var scrapeTasks = dueTargets
                        .Select(target => ScrapeTargetBoundedAsync(
                            target,
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

    private List<ScrapeTarget> GetMetricsTargets(DeploymentsInformations deployments)
    {
        var functions = deployments.Functions.ToDictionary(
            static function => function.Deployment,
            StringComparer.Ordinal);
        var result = new List<ScrapeTarget>();

        foreach (var (scope, urls) in deployments.GetMetricsTargets())
        {
            int interval = ResolveTargetInterval(functions.GetValueOrDefault(scope));
            result.AddRange(urls.Select(url => new ScrapeTarget(
                scope,
                scope,
                MetricsSourceScope.LocalSourceName,
                url,
                interval,
                IsExternal: false)));
        }

        var externalScopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (DeploymentInformation function in deployments.Functions)
        {
            ScaleConfig? scale = function.Scale;
            if (scale is null || scale.Triggers.Count == 0 || scale.Sources.Count == 0)
                continue;

            var sources = scale.Sources.ToDictionary(static source => source.Name, StringComparer.Ordinal);
            foreach (string sourceName in scale.Triggers
                         .Select(static trigger => trigger.Source)
                         .Where(static source => source is not null)
                         .Select(static source => source!)
                         .Distinct(StringComparer.Ordinal))
            {
                if (!sources.TryGetValue(sourceName, out ScaleSource? source))
                    continue;

                string scope = MetricsSourceScope.ForExternal(function.Deployment, sourceName);
                externalScopes.Add(scope);
                result.Add(new ScrapeTarget(
                    scope,
                    function.Deployment,
                    sourceName,
                    source.Url,
                    ResolveTargetInterval(function),
                    IsExternal: true));
            }
        }

        _externalHealthRegistry.RetainScopes(externalScopes);
        return result;
    }

    private int ResolveTargetInterval(DeploymentInformation? function)
    {
        if (!_hasDelayOverride && function?.Scale?.ScrapeIntervalMilliseconds is { } configured)
            return configured;
        return _scrapeIntervalMilliseconds;
    }

    private List<ScrapeTarget> SelectDueTargets(IReadOnlyCollection<ScrapeTarget> targets)
    {
        var currentScopes = targets.Select(static target => target.Scope).ToHashSet(StringComparer.Ordinal);
        foreach (string staleScope in _nextScrapeByScope.Keys
                     .Where(key => !currentScopes.Contains(key))
                     .ToArray())
        {
            _nextScrapeByScope.Remove(staleScope);
        }

        var now = Stopwatch.GetTimestamp();
        var dueTargets = new List<ScrapeTarget>();
        foreach (IGrouping<string, ScrapeTarget> group in targets.GroupBy(
                     static target => target.Scope,
                     StringComparer.Ordinal))
        {
            if (_nextScrapeByScope.TryGetValue(group.Key, out long nextScrape) && now < nextScrape)
                continue;

            ScrapeTarget first = group.First();
            _nextScrapeByScope[group.Key] = now +
                (long)(first.IntervalMilliseconds / 1_000.0 * Stopwatch.Frequency);
            dueTargets.AddRange(group);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Scraping metrics for function {Function}, source {Source}, with {TargetCount} targets",
                    first.Function,
                    first.Source,
                    group.Count());
            }
        }

        return dueTargets;
    }

    private async Task ScrapeTargetBoundedAsync(
        ScrapeTarget target,
        long timestamp,
        IReadOnlyCollection<string> requestedMetricNames,
        SemaphoreSlim concurrency,
        CancellationToken stoppingToken)
    {
        await concurrency.WaitAsync(stoppingToken);
        try
        {
            await ScrapeTargetAsync(target, timestamp, requestedMetricNames, stoppingToken);
        }
        finally
        {
            concurrency.Release();
        }
    }

    private async Task ScrapeTargetAsync(
        ScrapeTarget target,
        long timestamp,
        IReadOnlyCollection<string> requestedMetricNames,
        CancellationToken stoppingToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var targetIdentity = target.IsExternal ? "source" : GetTargetIdentityFromUrl(target.Url);
            if (string.IsNullOrEmpty(targetIdentity))
            {
                RecordFailure(target, "invalid_url");
                return;
            }

            var http = httpClientFactory.CreateClient(nameof(MetricsScrapingWorker));
            using var req = new HttpRequestMessage(HttpMethod.Get, target.Url);
            using var scrapeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            scrapeTimeout.CancelAfter(TimeSpan.FromSeconds(_metricsScrapingOptions.RequestTimeoutSeconds));
            using var resp = await http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                scrapeTimeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                RecordFailure(target, "http_status");
                return;
            }

            var contentLength = resp.Content.Headers.ContentLength;
            if (contentLength > _metricsScrapingOptions.MaxResponseBytes)
            {
                logger.LogWarning(
                    "Metrics scrape rejected for function {Function}, source {Source}: " +
                    "Content-Length {ContentLength} exceeds " +
                    "MaxResponseBytes {MaxResponseBytes}",
                    target.Function,
                    target.Source,
                    contentLength,
                    _metricsScrapingOptions.MaxResponseBytes);
                RecordFailure(target, "response_too_large");
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
                    "Metrics scrape rejected for function {Function}, source {Source}: " +
                    "Reason={Reason}, BytesRead={BytesRead}, " +
                    "LinesRead={LinesRead}",
                    target.Function,
                    target.Source,
                    parsed.Status,
                    parsed.BytesRead,
                    parsed.LinesRead);
                RecordFailure(target, "parse");
                return;
            }

            if (parsed.Metrics.Count > 0)
                metricsStore.Add(timestamp, target.Scope, targetIdentity, parsed.Metrics);
            if (target.IsExternal)
            {
                _externalHealthRegistry.RecordSuccess(
                    target.Scope,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    target.IntervalMilliseconds,
                    parsed.Metrics.Keys);
            }
            MetricsScrapingTelemetry.RecordSuccess(target.Function, target.Source);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Metrics scrape timed out after {TimeoutSeconds} seconds for function {Function}, source {Source}",
                _metricsScrapingOptions.RequestTimeoutSeconds,
                target.Function,
                target.Source);
            RecordFailure(target, "timeout");
        }
        catch (Exception exception)
        {
            if (target.IsExternal)
            {
                // HttpRequestException messages may contain the full request URI.
                // Keep configured query parameters out of logs.
                logger.LogWarning(
                    "Metrics scrape error for function {Function}, source {Source}: {ErrorType}",
                    target.Function,
                    target.Source,
                    exception.GetType().Name);
            }
            else
            {
                logger.LogWarning(
                    exception,
                    "Metrics scrape error for function {Function}, source {Source}",
                    target.Function,
                    target.Source);
            }
            RecordFailure(target, "exception");
        }
        finally
        {
            MetricsScrapingTelemetry.RecordDuration(
                target.Function,
                target.Source,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private void RecordFailure(ScrapeTarget target, string reason)
    {
        if (target.IsExternal)
        {
            _externalHealthRegistry.RecordFailure(
                target.Scope,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                target.IntervalMilliseconds);
        }
        MetricsScrapingTelemetry.RecordFailure(target.Function, target.Source, reason);
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
