using System.Collections.Concurrent;

namespace SlimFaas.Kubernetes;

internal sealed class PrometheusScalerProvider(
    PromQlMiniEvaluator evaluator,
    ExternalMetricsSourceStore? sources = null,
    int defaultScrapeIntervalMilliseconds = 2000,
    ILogger<PrometheusScalerProvider>? logger = null) : IScalerProvider
{
    private sealed record CachedQuery(CompiledPromQlQuery? Query);
    private readonly ConcurrentDictionary<string, CachedQuery> _queries = new(StringComparer.Ordinal);

    public ValueTask<ScalerResult> GetAsync(ScalerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Evaluate(context));
    }

    private ScalerResult Evaluate(ScalerContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Trigger.Query))
            return new(ScalerState.Misconfigured);
        try
        {
            var query = _queries.GetOrAdd(context.Trigger.Query, Compile).Query;
            if (query is null)
                return new(ScalerState.Misconfigured);

            double value;
            if (context.Trigger.Source is { } name)
            {
                var source = ExternalMetricsSource.Resolve(context.Namespace, context.Function, context.Configuration, name);
                if (source is null)
                    return new(ScalerState.Misconfigured);
                var observation = sources?.Get(source.Identity);
                if (observation is null)
                    return new(ScalerState.Unavailable);
                if (observation.State != ScalerState.Valid)
                    return new(observation.State);

                var interval = context.Configuration.ScrapeIntervalMilliseconds ?? defaultScrapeIntervalMilliseconds;
                var lookback = TimeSpan.FromMilliseconds(3L * interval);
                if (context.NowUnixSeconds - observation.LastSuccess >= lookback.TotalSeconds)
                    return new(ScalerState.Stale);

                value = evaluator.EvaluateExternal(query, context.NowUnixSeconds, source.Identity,
                    observation.LastSuccess, lookback);
            }
            else
            {
                value = evaluator.Evaluate(query, context.NowUnixSeconds, context.Function);
            }

            return double.IsFinite(value) && value >= 0
                ? new(ScalerState.Valid, value, value > 0)
                : new(ScalerState.InvalidMetric);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or ArgumentException)
        {
            logger?.LogWarning(exception, "Cannot evaluate scaling metric {Metric} for function {Function}",
                context.Trigger.MetricName, context.Function);
            return new(ScalerState.InvalidMetric);
        }
    }

    private static CachedQuery Compile(string query)
    {
        try { return new(PromQlQueryCompiler.Compile(query)); }
        catch (FormatException) { return new(null); }
    }
}
