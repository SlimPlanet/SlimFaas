using MemoryPack;
using SlimFaas.Kubernetes;
using SlimFaas.Workers;
using Xunit;

namespace SlimFaas.Tests.Workers;

public sealed class InMemoryMetricsStoreTests
{
    [Fact]
    public void Add_StoresOneSeriesWithABoundedPointBuffer()
    {
        var store = CreateStore(
            retentionSeconds: 1_800,
            maxSeries: 10,
            maxPointsPerSeries: 3,
            "requests_total");

        store.Add(0, "deployment-a", "10.0.0.1", Metric("requests_total", 1));
        store.Add(5, "deployment-a", "10.0.0.1", Metric("requests_total", 2));
        store.Add(10, "deployment-a", "10.0.0.1", Metric("requests_total", 3));
        store.Add(15, "deployment-a", "10.0.0.1", Metric("requests_total", 4));

        var visited = new List<(int Id, string Deployment, string PodIp, string MetricKey, MetricPoint[] Points)>();
        store.VisitSeries((id, deployment, podIp, metricKey, points) =>
            visited.Add((id, deployment, podIp, metricKey, points.ToArray())));

        var series = Assert.Single(visited);
        Assert.Equal("deployment-a", series.Deployment);
        Assert.Equal("10.0.0.1", series.PodIp);
        Assert.Equal("requests_total", series.MetricKey);
        Assert.Equal([5L, 10L, 15L], series.Points.Select(point => point.Timestamp));
        Assert.Equal([2D, 3D, 4D], series.Points.Select(point => point.Value));
    }

    [Fact]
    public void Add_EnforcesTheGlobalSeriesLimit()
    {
        var store = CreateStore(
            retentionSeconds: 1_800,
            maxSeries: 2,
            maxPointsPerSeries: 10,
            "metric_a",
            "metric_b",
            "metric_c");

        store.Add(
            100,
            "deployment-a",
            "10.0.0.1",
            new Dictionary<string, double>
            {
                ["metric_a"] = 1,
                ["metric_b"] = 2,
                ["metric_c"] = 3
            });

        Assert.Equal(2, store.SeriesCount);
        var storedMetrics = store.Snapshot()[100]["deployment-a"]["10.0.0.1"];
        Assert.Equal(2, storedMetrics.Count);
        Assert.False(storedMetrics.ContainsKey("metric_c"));
    }

    [Fact]
    public void Add_RemovesExpiredSeriesAndReleasesCapacity()
    {
        var store = CreateStore(
            retentionSeconds: 10,
            maxSeries: 1,
            maxPointsPerSeries: 10,
            "metric_a",
            "metric_b");

        store.Add(100, "deployment-a", "10.0.0.1", Metric("metric_a", 1));
        store.Add(111, "deployment-a", "10.0.0.1", Metric("metric_b", 2));

        Assert.Equal(1, store.SeriesCount);
        var snapshot = store.Snapshot();
        Assert.False(snapshot.ContainsKey(100));
        Assert.Equal(2, snapshot[111]["deployment-a"]["10.0.0.1"]["metric_b"]);
    }

    [Fact]
    public void CreateRecord_PreservesTheLegacyFormatAndRoundTrips()
    {
        var original = CreateStore(
            retentionSeconds: 1_800,
            maxSeries: 10,
            maxPointsPerSeries: 10,
            "requests_total");
        original.Add(
            100,
            "deployment-a",
            "10.0.0.1",
            Metric("requests_total{status=\"200\"}", 12));
        original.Add(
            105,
            "deployment-a",
            "10.0.0.1",
            Metric("requests_total{status=\"200\"}", 15));

        var serialized = MemoryPackSerializer.Serialize(original.CreateRecord());
        var legacyRecord = MemoryPackSerializer.Deserialize<MetricsStoreRecord>(serialized);
        Assert.NotNull(legacyRecord);

        var restored = CreateStore(
            retentionSeconds: 1_800,
            maxSeries: 10,
            maxPointsPerSeries: 10,
            "requests_total");
        restored.ReplaceFromRecord(legacyRecord);

        Assert.Equal(1, restored.SeriesCount);
        Assert.Equal(
            12,
            restored.Snapshot()[100]["deployment-a"]["10.0.0.1"]["requests_total{status=\"200\"}"]);
        Assert.Equal(
            15,
            restored.Snapshot()[105]["deployment-a"]["10.0.0.1"]["requests_total{status=\"200\"}"]);
    }

    [Fact]
    public void ReplaceFromRecord_AppliesRetentionPointAndSeriesLimits()
    {
        var record = new MetricsStoreRecord(
            new Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>
            {
                [0] = LegacyBucket(("metric_a", 0), ("metric_b", 0)),
                [10] = LegacyBucket(("metric_a", 10), ("metric_b", 10)),
                [20] = LegacyBucket(("metric_a", 20), ("metric_b", 20)),
                [30] = LegacyBucket(("metric_a", 30), ("metric_b", 30))
            });
        var store = CreateStore(
            retentionSeconds: 20,
            maxSeries: 1,
            maxPointsPerSeries: 2,
            "metric_a",
            "metric_b");

        store.ReplaceFromRecord(record);

        Assert.Equal(1, store.SeriesCount);
        var snapshot = store.Snapshot();
        Assert.Equal([20L, 30L], snapshot.Keys.Order());
        Assert.All(snapshot.Values, deployments =>
            Assert.True(deployments["deployment-a"]["10.0.0.1"].ContainsKey("metric_a")));
    }

    [Fact]
    public void PromQlEvaluator_ReadsSeriesDirectlyWithoutCallingSnapshot()
    {
        var store = new DirectOnlyMetricsStore(
            [
                new MetricPoint(100, 10),
                new MetricPoint(160, 70)
            ]);
        var evaluator = new PromQlMiniEvaluator(store);

        var result = evaluator.Evaluate("sum(rate(requests_total[1m]))", 160);

        Assert.Equal(1, result, 6);
        Assert.Equal(1, store.VisitCount);
    }

    [Fact]
    public void PromQlEvaluator_ExcludesStaleInstantSeriesFromMetricsStore()
    {
        var store = CreateStore(
            retentionSeconds: 1_800,
            maxSeries: 10,
            maxPointsPerSeries: 10,
            "queue_depth");
        store.Add(100, "deployment-a", "stale", Metric("queue_depth", 9));
        store.Add(120, "deployment-a", "fresh", Metric("queue_depth", 4));
        var evaluator = new PromQlMiniEvaluator(store, TimeSpan.FromSeconds(30));

        Assert.Equal(13, evaluator.Evaluate("sum(queue_depth)", 129));
        Assert.Equal(4, evaluator.Evaluate("sum(queue_depth)", 131));
    }

    private static InMemoryMetricsStore CreateStore(
        long retentionSeconds,
        int maxSeries,
        int maxPointsPerSeries,
        params string[] requestedNames)
        => new(
            new TestRequestedMetricsRegistry(requestedNames),
            retentionSeconds,
            maxSeries,
            maxPointsPerSeries);

    private static Dictionary<string, double> Metric(string name, double value)
        => new(StringComparer.Ordinal) { [name] = value };

    private static Dictionary<string, Dictionary<string, Dictionary<string, double>>> LegacyBucket(
        params (string Name, double Value)[] metrics)
        => new(StringComparer.Ordinal)
        {
            ["deployment-a"] = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal)
            {
                ["10.0.0.1"] = metrics.ToDictionary(
                    metric => metric.Name,
                    metric => metric.Value,
                    StringComparer.Ordinal)
            }
        };

    private sealed class TestRequestedMetricsRegistry(IEnumerable<string> requestedNames)
        : IRequestedMetricsRegistry
    {
        private readonly HashSet<string> _requestedNames = new(requestedNames, StringComparer.Ordinal);

        public void RegisterFromQuery(string promql)
        {
        }

        public bool IsRequestedKey(string metricKey)
        {
            var brace = metricKey.IndexOf('{');
            return _requestedNames.Contains(brace < 0 ? metricKey : metricKey[..brace]);
        }

        public IReadOnlyCollection<string> GetRequestedMetricNames() => _requestedNames;
    }

    private sealed class DirectOnlyMetricsStore(IReadOnlyList<MetricPoint> points) : IMetricsStore
    {
        public long? LatestTimestamp => points[^1].Timestamp;

        public int SeriesCount => 1;

        public int VisitCount { get; private set; }

        public void Add(
            long timestamp,
            string deployment,
            string podIp,
            IReadOnlyDictionary<string, double> metrics)
            => throw new NotSupportedException();

        public IReadOnlyDictionary<long,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string, double>>>> Snapshot()
            => throw new InvalidOperationException("PromQL must not create a full store snapshot.");

        public MetricsStoreRecord CreateRecord() => throw new NotSupportedException();

        public void ReplaceFromRecord(MetricsStoreRecord record) => throw new NotSupportedException();

        public void VisitSeries(MetricsSeriesVisitor visitor)
        {
            VisitCount++;
            visitor(1, "deployment-a", "10.0.0.1", "requests_total", points);
        }
    }
}
