using BenchmarkDotNet.Attributes;
using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class PromQlEvaluationBenchmarks
{
    private readonly CompiledPromQlQuery _instant = PromQlQueryCompiler.Compile("sum(queue)");
    private readonly CompiledPromQlQuery _rate = PromQlQueryCompiler.Compile("sum(rate(requests_total[1m]))");
    private PromQlMiniEvaluator _evaluator = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registry = new RequestedMetricsRegistry();
        registry.RegisterFromQuery("queue + requests_total");
        var store = new InMemoryMetricsStore(registry);
        for (int sample = 0; sample < 60; sample++)
        {
            for (int pod = 0; pod < 16; pod++)
            {
                store.Add(sample * 5, "worker", $"pod-{pod}", new Dictionary<string, double>
                {
                    ["""queue{job="worker"}"""] = sample,
                    ["""requests_total{job="worker"}"""] = sample + 10
                });
            }
        }

        _evaluator = new PromQlMiniEvaluator(store);
    }

    [Benchmark]
    public double Instant() => _evaluator.Evaluate(_instant, 295, "worker");

    [Benchmark]
    public double CounterRate() => _evaluator.Evaluate(_rate, 295, "worker");
}
