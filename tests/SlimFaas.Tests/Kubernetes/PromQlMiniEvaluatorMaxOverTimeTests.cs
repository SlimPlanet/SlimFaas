using Moq;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public class PromQlMiniEvaluatorMaxOverTimeTests
{
    [Fact]
    public void MaxOverTime_ShouldReturnMaxSampleWithinWindow()
    {
        // Arrange
        var ts1 = 100L;
        var ts2 = 110L;

        // metrics au timestamp 100 : 10
        var metricsAtTs1 = new Dictionary<string, double>
        {
            { "http_requests_total{method=\"GET\"}", 10.0 }
        };

        // metrics au timestamp 110 : 30
        var metricsAtTs2 = new Dictionary<string, double>
        {
            { "http_requests_total{method=\"GET\"}", 30.0 }
        };

        // pod -> metrics
        var podMapTs1 = new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            { "10.0.0.1", metricsAtTs1 }
        };
        var podMapTs2 = new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            { "10.0.0.1", metricsAtTs2 }
        };

        // deployment -> podMap
        var depMapTs1 = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
        {
            { "fibonacci1", podMapTs1 }
        };
        var depMapTs2 = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
        {
            { "fibonacci1", podMapTs2 }
        };

        // snapshot final : ts -> depMap
        var snapshot =
            new Dictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            {
                { ts1, depMapTs1 },
                { ts2, depMapTs2 }
            };

        // Moq sur le SnapshotProvider (delegate)
        var snapshotProviderMock = new Mock<PromQlMiniEvaluator.SnapshotProvider>();
        snapshotProviderMock
            .Setup(sp => sp())
            .Returns(snapshot);

        var evaluator = new PromQlMiniEvaluator(snapshotProviderMock.Object);

        // Act
        // Fenêtre [20s] => on voit les points 100 et 110, max = 30
        var result = evaluator.Evaluate("max_over_time(http_requests_total{method=\"GET\"}[20s])");

        // Assert
        Assert.Equal(30.0, result, 6);
        snapshotProviderMock.Verify(sp => sp(), Times.Once);
    }

    [Fact]
    public void SumOfMaxOverTime_ShouldSumEachPodMaximum()
    {
        var snapshot = new Dictionary<long,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string, double>>>>
        {
            [100] = new Dictionary<string,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
            {
                ["api"] = new Dictionary<string, IReadOnlyDictionary<string, double>>
                {
                    ["pod-a"] = new Dictionary<string, double> { ["work"] = 2 },
                    ["pod-b"] = new Dictionary<string, double> { ["work"] = 7 }
                }
            },
            [110] = new Dictionary<string,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
            {
                ["api"] = new Dictionary<string, IReadOnlyDictionary<string, double>>
                {
                    ["pod-a"] = new Dictionary<string, double> { ["work"] = 5 },
                    ["pod-b"] = new Dictionary<string, double> { ["work"] = 3 }
                }
            }
        };
        var evaluator = new PromQlMiniEvaluator(() => snapshot);

        Assert.Equal(12, evaluator.Evaluate("sum(max_over_time(work[20s]))", 110), 6);
        Assert.Equal(7, evaluator.Evaluate("max_over_time(work[20s])", 110), 6);
    }

    [Fact]
    public void DeploymentScope_ShouldExcludeIdenticallyNamedMetricsFromOtherFunctions()
    {
        var snapshot = new Dictionary<long,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string, double>>>>
        {
            [100] = new Dictionary<string,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
            {
                ["api-a"] = new Dictionary<string, IReadOnlyDictionary<string, double>>
                {
                    ["pod-a"] = new Dictionary<string, double> { ["work"] = 4 }
                },
                ["api-b"] = new Dictionary<string, IReadOnlyDictionary<string, double>>
                {
                    ["pod-b"] = new Dictionary<string, double> { ["work"] = 9 }
                }
            }
        };
        var evaluator = new PromQlMiniEvaluator(() => snapshot);

        Assert.Equal(13, evaluator.Evaluate("sum(work)", 100), 6);
        Assert.Equal(4, evaluator.Evaluate("sum(work)", 100, "api-a"), 6);
        Assert.Equal(9, evaluator.Evaluate("sum(work)", 100, "api-b"), 6);
    }
}
