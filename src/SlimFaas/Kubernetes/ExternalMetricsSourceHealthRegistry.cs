using System.Collections.Concurrent;

namespace SlimFaas.Kubernetes;

public sealed record ExternalMetricsSourceHealth(
    bool LastAttemptSucceeded,
    long LastAttemptUnixSeconds,
    long? LastSuccessUnixSeconds,
    int ScrapeIntervalMilliseconds,
    IReadOnlySet<string> ObservedMetricNames);

public interface IExternalMetricsSourceHealthRegistry
{
    void RecordSuccess(
        string scope,
        long timestampUnixSeconds,
        int scrapeIntervalMilliseconds,
        IEnumerable<string> observedMetricKeys);

    void RecordFailure(string scope, long timestampUnixSeconds, int scrapeIntervalMilliseconds);

    bool IsHealthy(
        string scope,
        long nowUnixSeconds,
        IReadOnlySet<string> requiredMetricNames,
        out string reason);

    bool TryGet(string scope, out ExternalMetricsSourceHealth? health);

    void RetainScopes(IReadOnlySet<string> scopes);
}

public sealed class ExternalMetricsSourceHealthRegistry : IExternalMetricsSourceHealthRegistry
{
    private readonly ConcurrentDictionary<string, ExternalMetricsSourceHealth> _states =
        new(StringComparer.Ordinal);

    public void RecordSuccess(
        string scope,
        long timestampUnixSeconds,
        int scrapeIntervalMilliseconds,
        IEnumerable<string> observedMetricKeys)
    {
        var names = observedMetricKeys
            .Select(GetMetricName)
            .Where(static name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        _states[scope] = new ExternalMetricsSourceHealth(
            true,
            timestampUnixSeconds,
            timestampUnixSeconds,
            scrapeIntervalMilliseconds,
            names);
    }

    public void RecordFailure(string scope, long timestampUnixSeconds, int scrapeIntervalMilliseconds)
    {
        _states.AddOrUpdate(
            scope,
            _ => new ExternalMetricsSourceHealth(
                false,
                timestampUnixSeconds,
                null,
                scrapeIntervalMilliseconds,
                new HashSet<string>(StringComparer.Ordinal)),
            (_, previous) => previous with
            {
                LastAttemptSucceeded = false,
                LastAttemptUnixSeconds = timestampUnixSeconds,
                ScrapeIntervalMilliseconds = scrapeIntervalMilliseconds
            });
    }

    public bool IsHealthy(
        string scope,
        long nowUnixSeconds,
        IReadOnlySet<string> requiredMetricNames,
        out string reason)
    {
        if (!_states.TryGetValue(scope, out ExternalMetricsSourceHealth? state))
        {
            reason = "source_not_scraped";
            return false;
        }

        if (!state.LastAttemptSucceeded || state.LastSuccessUnixSeconds is null)
        {
            reason = "last_scrape_failed";
            return false;
        }

        long freshnessSeconds = Math.Max(
            1,
            (state.ScrapeIntervalMilliseconds * 3L + 999L) / 1_000L);
        long ageSeconds = nowUnixSeconds - state.LastSuccessUnixSeconds.Value;
        if (ageSeconds < 0 || ageSeconds > freshnessSeconds)
        {
            reason = "source_stale";
            return false;
        }

        if (requiredMetricNames.Any(name => !state.ObservedMetricNames.Contains(name)))
        {
            reason = "metric_missing";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryGet(string scope, out ExternalMetricsSourceHealth? health) =>
        _states.TryGetValue(scope, out health);

    public void RetainScopes(IReadOnlySet<string> scopes)
    {
        foreach (string scope in _states.Keys)
        {
            if (!scopes.Contains(scope))
                _states.TryRemove(scope, out _);
        }
    }

    private static string GetMetricName(string metricKey)
    {
        int labelsStart = metricKey.IndexOf('{', StringComparison.Ordinal);
        return labelsStart < 0 ? metricKey : metricKey[..labelsStart];
    }
}
