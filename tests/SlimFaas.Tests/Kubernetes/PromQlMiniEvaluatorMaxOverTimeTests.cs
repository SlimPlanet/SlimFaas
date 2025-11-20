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
}
