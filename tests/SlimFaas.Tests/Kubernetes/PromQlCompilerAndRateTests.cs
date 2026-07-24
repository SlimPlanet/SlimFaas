// Focused tests for the PromQL compiler (PromQlQueryCompiler / CompiledPromQlQuery) and the
// shared rate calculator (PromQlRateCalculator), covering contracts not exercised elsewhere:
//
//  CR1 – Exact metric-name extraction from CompiledPromQlQuery.ReferencedMetricNames
//  CR2 – Plan reusability: a CompiledPromQlQuery evaluated multiple times, or across
//        different data snapshots, returns correct results without re-parsing.
//  CR3 – Shared rate behaviour: counter-reset and edge-case handling is identical for
//        rate(), per-bucket rate(), and avg(rate(...)).

using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class PromQlCompilerAndRateTests
{
    // ─── Snapshot builder ────────────────────────────────────────────────────

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
        IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> snap)
        => new((() => snap));

    // ─────────────────────────────────────────────────────────────────────────
    // CR1 – Exact metric-name extraction
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Compile_SingleSelectorQuery_ExtractsExactMetricName()
    {
        var compiled = PromQlQueryCompiler.Compile("""sum(rate(http_requests_total{job="api"}[1m]))""");

        Assert.Single(compiled.ReferencedMetricNames);
        Assert.Contains("http_requests_total", compiled.ReferencedMetricNames);
    }

    [Fact]
    public void Compile_BinaryQuery_ExtractsBothMetricNames()
    {
        var compiled = PromQlQueryCompiler.Compile(
            """sum(rate(requests_total[1m])) / sum(rate(errors_total[1m]))""");

        Assert.Equal(2, compiled.ReferencedMetricNames.Count);
        Assert.Contains("requests_total", compiled.ReferencedMetricNames);
        Assert.Contains("errors_total", compiled.ReferencedMetricNames);
    }

    [Fact]
    public void Compile_HistogramQuantileQuery_ExtractsBucketMetricName()
    {
        var compiled = PromQlQueryCompiler.Compile(
            """histogram_quantile(0.99, sum by (le)(rate(http_request_duration_seconds_bucket[5m])))""");

        Assert.Single(compiled.ReferencedMetricNames);
        Assert.Contains("http_request_duration_seconds_bucket", compiled.ReferencedMetricNames);
    }

    [Fact]
    public void Compile_MaxOverTimeQuery_ExtractsMetricName()
    {
        var compiled = PromQlQueryCompiler.Compile("""max_over_time(queue_depth{env="prod"}[10m])""");

        Assert.Single(compiled.ReferencedMetricNames);
        Assert.Contains("queue_depth", compiled.ReferencedMetricNames);
    }

    [Fact]
    public void Compile_LiteralScalarQuery_HasNoMetricNames()
    {
        var compiled = PromQlQueryCompiler.Compile("42");

        Assert.Empty(compiled.ReferencedMetricNames);
    }

    [Fact]
    public void Compile_DuplicateMetricInBinaryExpression_AppearsOnlyOnce()
    {
        // rate(m[1m]) + rate(m[2m]) – same metric name referenced twice
        var compiled = PromQlQueryCompiler.Compile("sum(rate(m[1m])) + sum(rate(m[2m]))");

        Assert.Single(compiled.ReferencedMetricNames);
        Assert.Contains("m", compiled.ReferencedMetricNames);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CR1b – Validate helper
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidQuery_ReturnsNull()
    {
        Assert.Null(PromQlQueryCompiler.Validate("sum(rate(m[1m]))"));
    }

    [Fact]
    public void Validate_InvalidQuery_ReturnsNonNullMessage()
    {
        var msg = PromQlQueryCompiler.Validate("sum(rate(m[1d]))");

        Assert.NotNull(msg);
        Assert.NotEmpty(msg);
    }

    [Fact]
    public void Validate_EmptyQuery_ReturnsNonNullMessage()
    {
        Assert.NotNull(PromQlQueryCompiler.Validate(""));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CR2 – Plan reusability
    // ─────────────────────────────────────────────────────────────────────────

    // A single CompiledPromQlQuery instance evaluated twice on the same snapshot
    // must return identical results.
    [Fact]
    public void CompiledQuery_EvaluatedTwice_ReturnsSameResult()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 60));
        var eval = SnapEval(snap);
        var compiled = PromQlQueryCompiler.Compile("""sum(rate(m{job="a"}[1m]))""");

        var r1 = eval.Evaluate(compiled, nowUnixSeconds: 160);
        var r2 = eval.Evaluate(compiled, nowUnixSeconds: 160);

        Assert.Equal(r1, r2, 6);
    }

    // The same CompiledPromQlQuery used with two different evaluators (different data)
    // produces different results – proving the plan is data-independent.
    [Fact]
    public void CompiledQuery_ReusedAcrossDifferentData_ReflectsEachDataset()
    {
        var snapA = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 60));   // rate = 1.0

        var snapB = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 120));  // rate = 2.0

        var compiled = PromQlQueryCompiler.Compile("""sum(rate(m{job="a"}[1m]))""");

        var evalA = SnapEval(snapA);
        var evalB = SnapEval(snapB);

        Assert.Equal(1.0, evalA.Evaluate(compiled, nowUnixSeconds: 160), 6);
        Assert.Equal(2.0, evalB.Evaluate(compiled, nowUnixSeconds: 160), 6);
    }

    // Evaluate(CompiledPromQlQuery) must produce the same scalar as Evaluate(string)
    // for the same query.
    [Fact]
    public void Evaluate_CompiledOverload_MatchesStringOverload()
    {
        var snap = BuildSnapshot(
            (100, "d", "p", "m{job=\"a\"}", 0),
            (160, "d", "p", "m{job=\"a\"}", 90));
        var eval = SnapEval(snap);
        const string q = """sum(rate(m{job="a"}[1m]))""";

        var fromString = eval.Evaluate(q, nowUnixSeconds: 160);
        var fromCompiled = eval.Evaluate(PromQlQueryCompiler.Compile(q), nowUnixSeconds: 160);

        Assert.Equal(fromString, fromCompiled, 6);
    }

    // CompiledPromQlQuery.ReferencedMetricNames must be immutable (IReadOnlySet).
    [Fact]
    public void CompiledQuery_ReferencedMetricNames_IsReadOnly()
    {
        var compiled = PromQlQueryCompiler.Compile("sum(rate(m[1m]))");
        Assert.IsAssignableFrom<IReadOnlySet<string>>(compiled.ReferencedMetricNames);
    }

    // Evaluate(CompiledPromQlQuery = null) must throw ArgumentNullException.
    [Fact]
    public void Evaluate_NullCompiledQuery_ThrowsArgumentNullException()
    {
        var eval = SnapEval(BuildSnapshot((1, "d", "p", "m", 1)));
        Assert.Throws<ArgumentNullException>(() => eval.Evaluate((CompiledPromQlQuery)null!));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CR3 – Shared rate behaviour (counter-reset handling is consistent)
    // ─────────────────────────────────────────────────────────────────────────

    // PromQlRateCalculator.TryComputeRate: normal two-point series.
    [Fact]
    public void RateCalculator_TwoPoints_ComputesCorrectRate()
    {
        var sl = new SortedList<long, double> { { 0, 10 }, { 60, 70 } };
        Assert.True(PromQlRateCalculator.TryComputeRate(sl, out var rate));
        Assert.Equal(1.0, rate, 6);
    }

    // PromQlRateCalculator.TryComputeRate: single point → false.
    [Fact]
    public void RateCalculator_SinglePoint_ReturnsFalse()
    {
        var sl = new SortedList<long, double> { { 0, 5 } };
        Assert.False(PromQlRateCalculator.TryComputeRate(sl, out _));
    }

    // PromQlRateCalculator.TryComputeRate: empty series → false.
    [Fact]
    public void RateCalculator_EmptySeries_ReturnsFalse()
    {
        var sl = new SortedList<long, double>();
        Assert.False(PromQlRateCalculator.TryComputeRate(sl, out _));
    }

    // PromQlRateCalculator.TryComputeRate: counter reset (negative delta) → false.
    [Fact]
    public void RateCalculator_CounterReset_ReturnsFalse()
    {
        var sl = new SortedList<long, double> { { 0, 100 }, { 60, 10 } }; // reset
        Assert.False(PromQlRateCalculator.TryComputeRate(sl, out _));
    }

    // rate() excludes resets → NaN when all series reset.
    [Fact]
    public void Rate_AllSeriesReset_ReturnsNaN()
    {
        var snap = BuildSnapshot(
            (0, "d", "p", "cnt{job=\"a\"}", 100),
            (60, "d", "p", "cnt{job=\"a\"}", 10)); // reset
        var eval = SnapEval(snap);
        Assert.True(double.IsNaN(eval.Evaluate("""sum(rate(cnt{job="a"}[1m]))""", nowUnixSeconds: 60)));
    }

    // avg(rate(...)) excludes resets → NaN when all series reset.
    [Fact]
    public void AvgRate_AllSeriesReset_ReturnsNaN()
    {
        var snap = BuildSnapshot(
            (0, "d", "p", "cnt{job=\"a\"}", 100),
            (60, "d", "p", "cnt{job=\"a\"}", 10)); // reset
        var eval = SnapEval(snap);
        Assert.True(double.IsNaN(eval.Evaluate("""avg(rate(cnt{job="a"}[1m]))""", nowUnixSeconds: 60)));
    }

    // rate() and avg(rate(...)) return the same value for a single non-reset series
    // (both reduce to the same calculation for one valid series).
    [Fact]
    public void Rate_AndAvgRate_AgreeForSingleNonResetSeries()
    {
        var snap = BuildSnapshot(
            (0, "d", "p", "m{job=\"a\"}", 0),
            (60, "d", "p", "m{job=\"a\"}", 60));
        var eval = SnapEval(snap);
        const long now = 60;
        var rateResult = eval.Evaluate("""sum(rate(m{job="a"}[1m]))""", nowUnixSeconds: now);
        var avgRateResult = eval.Evaluate("""avg(rate(m{job="a"}[1m]))""", nowUnixSeconds: now);

        Assert.Equal(rateResult, avgRateResult, 6);
    }

    // RatePerBucketNode (used by histogram_quantile) also excludes reset buckets.
    // A bucket that decreases contributes nothing; only increasing buckets contribute.
    [Fact]
    public void RatePerBucket_ResetBucket_IsExcluded()
    {
        var snap = BuildSnapshot(
            (0, "d", "p", """http_req_bucket{le="0.1"}""", 100),
            (60, "d", "p", """http_req_bucket{le="0.1"}""", 10), // reset – excluded
            (0, "d", "p", """http_req_bucket{le="+Inf"}""", 200),
            (60, "d", "p", """http_req_bucket{le="+Inf"}""", 260)); // rate = 1.0
        var eval = SnapEval(snap);

        // histogram_quantile: only the +Inf bucket has a valid rate.
        // With only one bucket the quantile resolves to that bucket's upper bound.
        var res = eval.Evaluate(
            """histogram_quantile(0.99, sum by (le)(rate(http_req_bucket[1m])))""",
            nowUnixSeconds: 60);

        Assert.False(double.IsNaN(res));
    }
}
