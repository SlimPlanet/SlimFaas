// Characterization tests that lock in the current behavioral contracts of PromQlMiniEvaluator
// before the planned compiler / cache / index refactor.
//
// Contracts locked:
//  C1 – Constructor: null-argument and invalid-lookback guards
//  C2 – Parser: exact exception type for malformed inputs not yet covered elsewhere
//  C3 – Selector / label-matcher precision
//  C4 – Rate and no-data edge cases
//  C5 – IMetricsStore data source (parity with snapshot path + own edge cases)
//  C6 – Reusable / immutable evaluation expectations

using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Kubernetes;

public sealed class PromQlMiniEvaluatorCharacterizationTests
{
    // ─── Shared snapshot builder (same shape as other evaluator test files) ──

    private static IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
        BuildSnapshot(params (long ts, string dep, string pod, string key, double value)[] points)
    {
        var store = new Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>();
        foreach (var p in points)
        {
            if (!store.TryGetValue(p.ts, out var depMap))
                depMap = store[p.ts] = new(StringComparer.Ordinal);
            if (!depMap.TryGetValue(p.dep, out var podMap))
                podMap = depMap[p.dep] = new(StringComparer.Ordinal);
            if (!podMap.TryGetValue(p.pod, out var metrics))
                metrics = podMap[p.pod] = new(StringComparer.Ordinal);
            metrics[p.key] = p.value;
        }
        return store.ToDictionary(
            t => t.Key,
            t => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>)
                t.Value.ToDictionary(
                    d => d.Key,
                    d => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>)
                        d.Value.ToDictionary(
                            p => p.Key,
                            p => (IReadOnlyDictionary<string, double>)p.Value.ToDictionary(m => m.Key, m => m.Value))));
    }

    private static PromQlMiniEvaluator SnapEval(
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> snap,
        TimeSpan? lookback = null)
        => new((() => snap), lookback);

    // ─────────────────────────────────────────────────────────────────────────
    // C1 – Constructor guards
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullSnapshotProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PromQlMiniEvaluator((PromQlMiniEvaluator.SnapshotProvider)null!));
    }

    [Fact]
    public void Constructor_NullMetricsStore_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new PromQlMiniEvaluator((IMetricsStore)null!));
    }

    [Fact]
    public void Constructor_ZeroLookback_ThrowsArgumentOutOfRangeException()
    {
        var snap = BuildSnapshot((1, "d", "p", "m", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PromQlMiniEvaluator(() => snap, TimeSpan.Zero));
    }

    [Fact]
    public void Constructor_NegativeLookback_ThrowsArgumentOutOfRangeException()
    {
        var snap = BuildSnapshot((1, "d", "p", "m", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PromQlMiniEvaluator(() => snap, TimeSpan.FromSeconds(-1)));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C2 – Parser exception contract
    // ─────────────────────────────────────────────────────────────────────────

    // Contract: unsupported operators !=  →  FormatException
    [Fact]
    public void Parser_NotEqualLabelMatcher_ThrowsFormatException()
    {
        var eval = SnapEval(BuildSnapshot((1, "d", "p", "m", 1)));
        // `!=` is not implemented; parser expects `=` or `=~` after a label name
        Assert.Throws<FormatException>(() =>
            eval.Evaluate("""sum(rate(m{job!="a"}[1m]))"""));
    }

    // Contract: empty query string  →  FormatException
    [Fact]
    public void Parser_EmptyQuery_ThrowsFormatException()
    {
        var eval = SnapEval(BuildSnapshot((1, "d", "p", "m", 1)));
        Assert.Throws<FormatException>(() => eval.Evaluate(""));
    }

    // Contract: duration unit 'd' (days) is unsupported  →  FormatException
    [Fact]
    public void Parser_DurationUnitDays_ThrowsFormatException()
    {
        var eval = SnapEval(BuildSnapshot((1, "d", "p", "m", 1)));
        Assert.Throws<FormatException>(() => eval.Evaluate("sum(rate(m[1d]))"));
    }

    // Contract: a standalone negative literal is not a valid expression  →  FormatException
    // The parser checks for digit/`(` before falling through to ParseIdent, and `-` triggers
    // "Identifier expected".
    [Fact]
    public void Parser_StandaloneNegativeLiteral_ThrowsFormatException()
    {
        var eval = SnapEval(BuildSnapshot((1, "d", "p", "m", 1)));
        Assert.Throws<FormatException>(() => eval.Evaluate("-1"));
    }

    // Contract: label block with no preceding metric name  →  FormatException
    // `{job="a"}` starts with `{`, which is not a letter; ParseIdent throws.
    [Fact]
    public void Parser_LabelBlockWithoutMetricName_ThrowsFormatException()
    {
        var eval = SnapEval(BuildSnapshot((1, "d", "p", "m", 1)));
        Assert.Throws<FormatException>(() => eval.Evaluate("""{job="a"}"""));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C3 – Selector / label-matcher precision
    // ─────────────────────────────────────────────────────────────────────────

    // Contract: a bare metric name with no label filter matches every series that
    // carries that name, regardless of what labels those series have.
    [Fact]
    public void Selector_NoLabels_MatchesAllSeriesWithThatMetricName()
    {
        var snap = BuildSnapshot(
            (100, "d", "p1", "http_total{job=\"a\"}", 10),
            (160, "d", "p1", "http_total{job=\"a\"}", 40),
            (100, "d", "p2", "http_total{job=\"b\"}", 5),
            (160, "d", "p2", "http_total{job=\"b\"}", 20));
        var eval = SnapEval(snap);
        // (40-10)/60 + (20-5)/60 = 0.5 + 0.25 = 0.75
        Assert.Equal(0.75, eval.Evaluate("sum(rate(http_total[1m]))", nowUnixSeconds: 160), 6);
    }

    // Contract: a series that carries labels beyond those in the selector still matches
    // (subset semantics – the extra labels are ignored by the matcher).
    [Fact]
    public void Selector_ExtraLabelOnSeries_DoesNotPreventMatch()
    {
        var snap = BuildSnapshot(
            (100, "d", "p1", "req_total{job=\"svc\",status=\"200\"}", 0),
            (160, "d", "p1", "req_total{job=\"svc\",status=\"200\"}", 60));
        var eval = SnapEval(snap);
        var res = eval.Evaluate("""sum(rate(req_total{job="svc"}[1m]))""", nowUnixSeconds: 160);
        Assert.Equal(1.0, res, 6);
    }

    // Contract: a series that is missing a label required by the selector is excluded.
    [Fact]
    public void Selector_MissingRequiredLabel_ExcludesSeries()
    {
        var snap = BuildSnapshot(
            (100, "d", "p1", "req_total", 0),   // no `job` label
            (160, "d", "p1", "req_total", 120));
        var eval = SnapEval(snap);
        var res = eval.Evaluate("""sum(rate(req_total{job="svc"}[1m]))""", nowUnixSeconds: 160);
        Assert.True(double.IsNaN(res));
    }

    // Contract: the =~ matcher anchors the pattern to the full label value (^pattern$),
    // so "web" does not match "web-api" unless the pattern explicitly allows it.
    [Fact]
    public void Selector_RegexIsAnchoredToFullLabelValue()
    {
        var snap = BuildSnapshot(
            (100, "d", "p1", "http_total{job=\"web\"}", 0),
            (160, "d", "p1", "http_total{job=\"web\"}", 60),
            (100, "d", "p2", "http_total{job=\"web-api\"}", 0),
            (160, "d", "p2", "http_total{job=\"web-api\"}", 60));
        var eval = SnapEval(snap);

        // Exact-word pattern: only "web" matches
        var resExact = eval.Evaluate("""sum(rate(http_total{job=~"web"}[1m]))""", nowUnixSeconds: 160);
        Assert.Equal(1.0, resExact, 6);

        // Wildcard pattern: both match
        var resBoth = eval.Evaluate("""sum(rate(http_total{job=~"web.*"}[1m]))""", nowUnixSeconds: 160);
        Assert.Equal(2.0, resBoth, 6);
    }

    // Contract: multiple label matchers in a selector are combined with AND semantics.
    [Fact]
    public void Selector_MultiLabelMatcher_RequiresAllConditionsSatisfied()
    {
        var snap = BuildSnapshot(
            (100, "d", "p1", "req_total{job=\"a\",status=\"200\"}", 0),
            (160, "d", "p1", "req_total{job=\"a\",status=\"200\"}", 60),
            (100, "d", "p2", "req_total{job=\"a\",status=\"500\"}", 0),
            (160, "d", "p2", "req_total{job=\"a\",status=\"500\"}", 120),
            (100, "d", "p3", "req_total{job=\"b\",status=\"200\"}", 0),
            (160, "d", "p3", "req_total{job=\"b\",status=\"200\"}", 60));
        var eval = SnapEval(snap);
        // Only p1 satisfies both job="a" AND status="200"
        var res = eval.Evaluate("""sum(rate(req_total{job="a",status="200"}[1m]))""", nowUnixSeconds: 160);
        Assert.Equal(1.0, res, 6);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C4 – Rate and no-data edge cases
    // ─────────────────────────────────────────────────────────────────────────

    // Contract: when every matched series has a negative delta (all reset), rate() → NaN.
    [Fact]
    public void Rate_AllSeriesReset_ReturnsNaN()
    {
        var snap = BuildSnapshot(
            (100, "d", "p1", "req_total{job=\"a\"}", 50),
            (160, "d", "p1", "req_total{job=\"a\"}", 10),
            (100, "d", "p2", "req_total{job=\"a\"}", 80),
            (160, "d", "p2", "req_total{job=\"a\"}", 30));
        var eval = SnapEval(snap);
        Assert.True(double.IsNaN(eval.Evaluate("""sum(rate(req_total{job="a"}[1m]))""", nowUnixSeconds: 160)));
    }

    // Contract: avg(rate(...)) with all resets → NaN (no valid series).
    [Fact]
    public void AvgRate_AllSeriesReset_ReturnsNaN()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "req_total{job=\"a\"}", 50),
            (160, "d", "p", "req_total{job=\"a\"}", 10));
        var eval = SnapEval(snap);
        Assert.True(double.IsNaN(eval.Evaluate("""avg(rate(req_total{job="a"}[1m]))""", nowUnixSeconds: 160)));
    }

    // Contract: NaN from one sub-expression propagates through binary arithmetic.
    [Fact]
    public void Arithmetic_NaNFromMissingSeriesPropagatesToResult()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "good_total{job=\"a\"}", 0),
            (160, "d", "p", "good_total{job=\"a\"}", 60));
        var eval = SnapEval(snap);
        // sum(rate(no_such_metric[1m])) → NaN; NaN + 1.0 → NaN (IEEE 754)
        var res = eval.Evaluate(
            """sum(rate(no_such_metric[1m])) + sum(rate(good_total{job="a"}[1m]))""",
            nowUnixSeconds: 160);
        Assert.True(double.IsNaN(res));
    }

    // Contract: passing nowUnixSeconds=null uses the maximum timestamp present in the snapshot.
    [Fact]
    public void NowUnixSeconds_NullUsesMaxSnapshotTimestamp()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 60));
        var eval = SnapEval(snap);
        const string q = """sum(rate(m{job="a"}[1m]))""";
        // null ≡ nowUnixSeconds=160 (the max key)
        Assert.Equal(eval.Evaluate(q, nowUnixSeconds: 160), eval.Evaluate(q, nowUnixSeconds: null), 6);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C5 – IMetricsStore data source
    // ─────────────────────────────────────────────────────────────────────────

    // Minimal IMetricsStore stub: accepts pre-built series lists without InMemoryMetricsStore's
    // registry/retention overhead so we can drive the IMetricsStore path directly.
    private sealed class StubMetricsStore : IMetricsStore
    {
        private readonly (string dep, string pod, string key, MetricPoint[] pts)[] _series;

        public StubMetricsStore(
            long? latestTs,
            params (string dep, string pod, string key, MetricPoint[] pts)[] series)
        {
            LatestTimestamp = latestTs;
            _series = series;
        }

        public long? LatestTimestamp { get; }
        public int SeriesCount => _series.Length;

        public void Add(long ts, string dep, string pod, IReadOnlyDictionary<string, double> m)
            => throw new NotSupportedException();

        public IReadOnlyDictionary<long,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            Snapshot() => throw new NotSupportedException();

        public MetricsStoreRecord CreateRecord() => throw new NotSupportedException();
        public void ReplaceFromRecord(MetricsStoreRecord record) => throw new NotSupportedException();

        public void VisitSeries(MetricsSeriesVisitor visitor)
        {
            for (int i = 0; i < _series.Length; i++)
            {
                var (dep, pod, key, pts) = _series[i];
                visitor(i, dep, pod, key, pts);
            }
        }
    }

    // Contract: when LatestTimestamp is null the evaluator short-circuits and returns NaN.
    [Fact]
    public void MetricsStore_NoLatestTimestamp_ReturnsNaN()
    {
        var store = new StubMetricsStore(latestTs: null);
        var eval = new PromQlMiniEvaluator(store);
        Assert.True(double.IsNaN(eval.Evaluate("sum(rate(m[1m]))")));
    }

    // Contract: the IMetricsStore path produces the same scalar as the snapshot path
    // when fed identical data.
    [Fact]
    public void MetricsStore_SumRate_MatchesSnapshotPath()
    {
        const string metricKey = "http_requests_total{job=\"svc\"}";

        var storeEval = new PromQlMiniEvaluator(new StubMetricsStore(
            latestTs: 160,
            ("dep-a", "10.0.0.1", metricKey,
             [new MetricPoint(100, 10), new MetricPoint(160, 70)])));

        var snapEval = SnapEval(BuildSnapshot(
            (100, "dep-a", "10.0.0.1", metricKey, 10),
            (160, "dep-a", "10.0.0.1", metricKey, 70)));

        const string q = """sum(rate(http_requests_total{job="svc"}[1m]))""";
        Assert.Equal(snapEval.Evaluate(q, nowUnixSeconds: 160),
                     storeEval.Evaluate(q, nowUnixSeconds: 160), 6);
    }

    // Contract: the IMetricsStore path respects the instant-selector lookback
    // and excludes points that fall outside the window.
    [Fact]
    public void MetricsStore_InstantSelector_RespectsLookback()
    {
        // lookback=30s; at t=131 the point at t=100 is more than 30s old → excluded
        var store = new StubMetricsStore(
            latestTs: 131,
            ("d", "stale", "queue_depth", [new MetricPoint(100, 9)]),
            ("d", "fresh", "queue_depth", [new MetricPoint(120, 4)]));
        var eval = new PromQlMiniEvaluator(store, TimeSpan.FromSeconds(30));

        Assert.Equal(4.0, eval.Evaluate("sum(queue_depth)", nowUnixSeconds: 131), 6);
    }

    // Contract: a series whose metric name does not match the selector is excluded.
    [Fact]
    public void MetricsStore_NoMatchingSeries_ReturnsNaN()
    {
        var store = new StubMetricsStore(
            latestTs: 160,
            ("d", "p", "known_metric",
             [new MetricPoint(100, 10), new MetricPoint(160, 70)]));
        var eval = new PromQlMiniEvaluator(store);
        Assert.True(double.IsNaN(eval.Evaluate("sum(rate(other_metric[1m]))", nowUnixSeconds: 160)));
    }

    // Contract: max_over_time via IMetricsStore selects the maximum raw sample in the window.
    [Fact]
    public void MetricsStore_MaxOverTime_ReturnsMaxSampleInWindow()
    {
        var store = new StubMetricsStore(
            latestTs: 120,
            ("d", "p", "queue_depth{job=\"a\"}",
             [new MetricPoint(100, 5), new MetricPoint(110, 12), new MetricPoint(120, 8)]));
        var eval = new PromQlMiniEvaluator(store);
        var res = eval.Evaluate("""max_over_time(queue_depth{job="a"}[30s])""", nowUnixSeconds: 120);
        Assert.Equal(12.0, res, 6);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // C6 – Reusable / immutable evaluation expectations
    // ─────────────────────────────────────────────────────────────────────────

    // Contract: calling Evaluate twice with the same query and same nowUnixSeconds
    // on the same evaluator returns the identical scalar.
    [Fact]
    public void Evaluate_CalledTwice_ReturnsSameResult()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 10),
            (160, "d", "p", "m{job=\"a\"}", 70));
        var eval = SnapEval(snap);
        const string q = """sum(rate(m{job="a"}[1m]))""";
        Assert.Equal(eval.Evaluate(q, nowUnixSeconds: 160), eval.Evaluate(q, nowUnixSeconds: 160), 6);
    }

    // Contract: the snapshot provider delegate is called on every Evaluate() call.
    // A new snapshot returned by the delegate is immediately reflected in the next result.
    [Fact]
    public void Evaluate_SnapshotProviderCalledFreshEachCall()
    {
        var snapV1 = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 60));   // rate = 1.0
        var snapV2 = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 120));  // rate = 2.0

        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> current = snapV1;
        var eval = new PromQlMiniEvaluator(() => current);

        const string q = """sum(rate(m{job="a"}[1m]))""";
        var r1 = eval.Evaluate(q, nowUnixSeconds: 160);
        current = snapV2;
        var r2 = eval.Evaluate(q, nowUnixSeconds: 160);

        Assert.Equal(1.0, r1, 6);
        Assert.Equal(2.0, r2, 6);
    }

    // Contract: the provider delegate is invoked exactly once per Evaluate() call
    // (it is never cached between calls).
    [Fact]
    public void Evaluate_SnapshotProviderInvokedOncePerCall()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 10),
            (160, "d", "p", "m{job=\"a\"}", 70));
        int callCount = 0;
        var eval = new PromQlMiniEvaluator(() => { callCount++; return snap; });

        eval.Evaluate("""sum(rate(m{job="a"}[1m]))""", nowUnixSeconds: 160);
        Assert.Equal(1, callCount);

        eval.Evaluate("""sum(rate(m{job="a"}[1m]))""", nowUnixSeconds: 160);
        Assert.Equal(2, callCount);
    }

}
