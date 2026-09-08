using SlimFaas.Kubernetes;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Kubernetes;

public sealed class PromQlQueueScopeTests
{
    private static readonly string[] QueueMetrics =
    [
        "slimfaas_function_queue_ready_items",
        "slimfaas_function_queue_in_flight_items",
        "slimfaas_function_queue_retry_pending_items"
    ];

    private static PromQlMiniEvaluator CreateEvaluator(bool indexedStore, double ownQueue = 20)
    {
        var registry = new RequestedMetricsRegistry();
        foreach (var metric in QueueMetrics) registry.RegisterMetricName(metric);
        registry.RegisterMetricName("requests_total");
        registry.RegisterMetricName("control_plane_metric");
        var store = new InMemoryMetricsStore(registry);
        foreach (var metric in QueueMetrics)
        {
            store.Add(1000, "slimfaas", "slimfaas-0", new Dictionary<string, double>
            {
                [$"{metric}{{function=\"fibonacci1\"}}"] = ownQueue,
                [$"{metric}{{function=\"other\"}}"] = 900,
                [metric] = 500 // Missing function label must not enter a scoped result.
            });
            store.Add(1000, "other", "other-0", new Dictionary<string, double>
            {
                [$"{metric}{{function=\"fibonacci1\"}}"] = 9999
            });
        }
        store.Add(1000, "fibonacci1", "function-0", new Dictionary<string, double> { ["requests_total"] = 4 });
        store.Add(1000, "other", "other-0", new Dictionary<string, double> { ["requests_total"] = 999 });
        store.Add(1000, "slimfaas", "slimfaas-0", new Dictionary<string, double>
        {
            ["requests_total{function=\"fibonacci1\"}"] = 500,
            ["control_plane_metric{function=\"fibonacci1\"}"] = 700
        });
        return indexedStore ? new PromQlMiniEvaluator(store) : new PromQlMiniEvaluator(store.Snapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopedQueueSelectorsIncludeOnlyOwnQueueFromSlimFaas(bool indexedStore)
    {
        var evaluator = CreateEvaluator(indexedStore);
        foreach (var metric in QueueMetrics)
        {
            Assert.Equal(20, evaluator.Evaluate($"sum({metric})", 1000, "fibonacci1"));
            Assert.Equal(20, evaluator.Evaluate($"max_over_time({metric}[30s])", 1000, "fibonacci1"));
            Assert.Equal(20, evaluator.Evaluate($"sum({metric}{{function=~\".*\"}})", 1000, "fibonacci1"));
            Assert.True(double.IsNaN(evaluator.Evaluate($"max_over_time({metric}{{function=\"other\"}}[30s])", 1000, "fibonacci1")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplicationAndUnrelatedControlPlaneMetricsRemainIsolated(bool indexedStore)
    {
        var evaluator = CreateEvaluator(indexedStore);
        Assert.Equal(4, evaluator.Evaluate("sum(requests_total)", 1000, "fibonacci1"));
        Assert.True(double.IsNaN(evaluator.Evaluate("sum(control_plane_metric)", 1000, "fibonacci1")));
        Assert.True(evaluator.Evaluate("sum(requests_total)", 1000) > 4);
    }

    [Theory]
    [InlineData(false, 80, 8)]
    [InlineData(true, 80, 8)]
    [InlineData(false, 0, 0)]
    [InlineData(true, 0, 0)]
    public void AutoScalerUsesItsOwnQueueForScaleOutAndScaleToZero(bool indexedStore, double queue, int expected)
    {
        var scaler = new AutoScaler(CreateEvaluator(indexedStore, queue), new InMemoryAutoScalerStore());
        var config = new ScaleConfig
        {
            ReplicaMax = 10,
            Triggers = [new ScaleTrigger(ScaleMetricType.Value, "queue", $"max_over_time({QueueMetrics[0]}[30s])", 10)],
            Behavior = new ScaleBehavior
            {
                ScaleUp = new ScaleDirectionBehavior(),
                ScaleDown = new ScaleDirectionBehavior()
            }
        };
        Assert.Equal(expected, scaler.ComputeDesiredReplicas("fibonacci1", config, 1, 0, 10, 1000));
    }
}
