using System.Globalization;
using System.Text.Json;
using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Kubernetes;

public class PromQlConformanceTests
{
    // The same file runs unchanged with "promtool test rules". Each expression
    // returns at most one sample so we can compare the public scalar result.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvaluatePrometheusRuleFixtures(bool snapshot)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Kubernetes", "Fixtures", "promql-conformance.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var group in fixture.RootElement.GetProperty("tests").EnumerateArray())
        {
            var registry = new RequestedMetricsRegistry();
            var store = new InMemoryMetricsStore(registry);
            var cases = group.GetProperty("promql_expr_test");
            foreach (var test in cases.EnumerateArray()) registry.RegisterFromQuery(test.GetProperty("expr").GetString()!);

            long step = Seconds(group.GetProperty("interval"));
            foreach (var series in group.GetProperty("input_series").EnumerateArray())
            {
                var key = series.GetProperty("series").GetString()!;
                long time = 0;
                foreach (var sample in series.GetProperty("values").GetString()!.Split(' '))
                {
                    if (sample != "_") store.Add(time, "dep", "pod", new Dictionary<string, double>
                    {
                        [key] = double.Parse(sample, CultureInfo.InvariantCulture)
                    });
                    time += step;
                }
            }

            var evaluator = snapshot ? new PromQlMiniEvaluator(store.Snapshot) : new PromQlMiniEvaluator(store);
            foreach (var test in cases.EnumerateArray())
            {
                var query = test.GetProperty("expr").GetString()!;
                double actual = evaluator.Evaluate(query, Seconds(test.GetProperty("eval_time")));
                var samples = test.GetProperty("exp_samples");
                double expected = samples.GetArrayLength() == 0 ? double.NaN : Assert.Single(samples.EnumerateArray())
                    .GetProperty("value").GetDouble();
                Assert.True(expected.Equals(actual) || Math.Abs(expected - actual) < 1e-12,
                    $"{query}: expected {expected}, got {actual}");
            }
        }
    }

    private static long Seconds(JsonElement duration) => long.Parse(duration.GetString()!.AsSpan()[..^1],
        CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderedEscapedLabelsRemainOneSeries(bool snapshot)
    {
        var registry = new RequestedMetricsRegistry();
        const string query = """sum(increase(requests_total{route="a,\"b\\c"}[30s]))""";
        registry.RegisterFromQuery(query);
        var store = new InMemoryMetricsStore(registry);
        store.Add(10, "dep", "pod", new Dictionary<string, double> { ["""requests_total{job="web",route="a,\"b\\c"}"""] = 10 });
        store.Add(20, "dep", "pod", new Dictionary<string, double> { ["""requests_total{route="a,\"b\\c",job="web"}"""] = 20 });
        var evaluator = snapshot ? new PromQlMiniEvaluator(store.Snapshot) : new PromQlMiniEvaluator(store);
        Assert.Equal(30, evaluator.Evaluate(query, 30));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsAmbiguousMatchesEvenForFilteredComparisons(bool snapshot)
    {
        var registry = new RequestedMetricsRegistry();
        registry.RegisterFromQuery("jobs + capacity");
        var store = new InMemoryMetricsStore(registry);
        foreach (string pod in new[] { "a", "b" })
            store.Add(10, "worker", pod, new Dictionary<string, double> { ["""jobs{queue="x"}"""] = 1 });
        store.Add(10, "worker", "c", new Dictionary<string, double> { ["""capacity{queue="x"}"""] = 3 });
        var evaluator = snapshot ? new PromQlMiniEvaluator(store.Snapshot) : new PromQlMiniEvaluator(store);
        Assert.Throws<FormatException>(() => evaluator.Evaluate("jobs > on(queue) capacity", 10));
        Assert.Throws<FormatException>(() => evaluator.Evaluate("capacity / ignoring() jobs", 10));
        Assert.Equal(2.0 / 3, evaluator.Evaluate("sum by(queue) (jobs) / on(queue) capacity", 10));
    }

    [Theory]
    [InlineData("count(0)")]
    [InlineData("sum by (job) (1)")]
    [InlineData("histogram_quantile(0.95, 1)")]
    [InlineData("histogram_quantile(queue, queue)")]
    [InlineData("clamp_min(1, queue)")]
    [InlineData("clamp_min(queue, queue)")]
    [InlineData("queue + on(job) 1")]
    [InlineData("1 > bool ignoring(pod) queue")]
    [InlineData("queue / on(job,job) queue")]
    [InlineData("queue / on(__name__) queue")]
    [InlineData("sum without(pod) (queue) by (job)")]
    [InlineData("queue{code=~\"[[:unknown:]]\"}")]
    [InlineData("1 < 2")]
    [InlineData("rate(m[0s])")]
    [InlineData("m{job=~\"[\"}")]
    [InlineData("m{job!~\"(?=a)\"}")]
    public void InvalidQueryRequestsNoMetrics(string query)
    {
        var registry = new RequestedMetricsRegistry();
        Assert.NotNull(PromQlQueryCompiler.Validate(query));
        registry.RegisterFromQuery(query);
        Assert.Empty(registry.GetRequestedMetricNames());
    }
}
