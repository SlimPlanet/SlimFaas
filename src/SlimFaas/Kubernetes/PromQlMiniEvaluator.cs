using SlimFaas.Workers;

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

    // Evaluates a raw query string. Checks for data availability before parsing (preserves
    // original NaN-on-no-data semantics when the store is empty).
    public double Evaluate(string query, long? nowUnixSeconds = null)
    {
        var ctx = BuildContext(nowUnixSeconds);
        if (ctx is null) return double.NaN;
        var compiled = PromQlQueryCompiler.Compile(query);
        return compiled.Root.Eval(ctx).AsScalar();
    }

    // Evaluates a pre-compiled query plan, skipping parsing on every call.
    public double Evaluate(CompiledPromQlQuery compiled, long? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        var ctx = BuildContext(nowUnixSeconds);
        if (ctx is null) return double.NaN;
        return compiled.Root.Eval(ctx).AsScalar();
    }

    private EvalContext? BuildContext(long? nowUnixSeconds)
    {
        if (_metricsStore is not null)
        {
            if (_metricsStore.LatestTimestamp is not { } latestTimestamp)
                return null;

            return new EvalContext(
                _metricsStore,
                nowUnixSeconds ?? latestTimestamp,
                _instantSelectorLookback);
        }

        var snapshot = _snapshotProvider!();
        if (snapshot.Count == 0)
            return null;

        return new EvalContext(
            snapshot,
            nowUnixSeconds ?? snapshot.Keys.Max(),
            _instantSelectorLookback);
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
