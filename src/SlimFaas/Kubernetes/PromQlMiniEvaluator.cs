using SlimFaas.Workers;
using ZLinq;

namespace SlimFaas.Kubernetes;

// Thin façade: builds the evaluation context and delegates to a compiled query plan.
// The heavy lifting (parsing, AST nodes, rate calculation) lives under PromQl/.
public sealed class PromQlMiniEvaluator
{
    private static readonly TimeSpan DefaultInstantSelectorLookback = TimeSpan.FromMinutes(5);

    public delegate IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> SnapshotProvider();

    private readonly SnapshotProvider? _snapshotProvider;
    private readonly IMetricsStore? _metricsStore;
    private readonly TimeSpan _instantSelectorLookback;

    public PromQlMiniEvaluator(
        SnapshotProvider snapshotProvider,
        TimeSpan? instantSelectorLookback = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _instantSelectorLookback = ValidateInstantSelectorLookback(instantSelectorLookback);
    }

    public PromQlMiniEvaluator(
        IMetricsStore metricsStore,
        TimeSpan? instantSelectorLookback = null)
    {
        _metricsStore = metricsStore ?? throw new ArgumentNullException(nameof(metricsStore));
        _instantSelectorLookback = ValidateInstantSelectorLookback(instantSelectorLookback);
    }

    public double Evaluate(string query, long? nowUnixSeconds = null, string? deployment = null) =>
        Evaluate(PromQlQueryCompiler.Compile(query), nowUnixSeconds, deployment);

    // Evaluates a pre-compiled query plan, skipping parsing on every call.
    public double Evaluate(
        CompiledPromQlQuery compiled,
        long? nowUnixSeconds = null,
        string? deployment = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var ctx = BuildContext(nowUnixSeconds, deployment);
        return compiled.Root.Eval(ctx).AsScalar();
    }

    internal double EvaluateExternal(CompiledPromQlQuery query, long nowUnixSeconds,
        string sourceIdentity, long scrapeTimestamp, TimeSpan lookback)
    {
        EvalContext context;
        if (_metricsStore is not null)
            context = new EvalContext(_metricsStore, nowUnixSeconds, lookback,
                externalSource: sourceIdentity, requiredTimestamp: scrapeTimestamp);
        else
            context = new EvalContext(_snapshotProvider!(), nowUnixSeconds, lookback,
                externalSource: sourceIdentity, requiredTimestamp: scrapeTimestamp);
        return query.Root.Eval(context).AsScalar();
    }

    private EvalContext BuildContext(long? nowUnixSeconds, string? deployment)
    {
        if (_metricsStore is not null)
        {
            return new EvalContext(
                _metricsStore,
                nowUnixSeconds ?? _metricsStore.LatestTimestamp ?? 0,
                _instantSelectorLookback,
                deployment);
        }

        var snapshot = _snapshotProvider!();
        return new EvalContext(
            snapshot,
            nowUnixSeconds ?? (snapshot.Count == 0 ? 0 : snapshot.Keys.AsValueEnumerable().Max()),
            _instantSelectorLookback,
            deployment);
    }

    private static TimeSpan ValidateInstantSelectorLookback(TimeSpan? configured)
    {
        var lookback = configured ?? DefaultInstantSelectorLookback;
        if (lookback <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(configured),
                lookback,
                "Instant selector lookback must be positive.");
        return lookback;
    }
}
