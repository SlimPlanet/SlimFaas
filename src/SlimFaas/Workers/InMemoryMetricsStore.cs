using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Workers;

[MemoryPackable]
public sealed partial record MetricsStoreRecord(
    Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>> Store)
{
    public static MetricsStoreRecord FromSnapshot(
        IReadOnlyDictionary<long,
            IReadOnlyDictionary<string,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string, double>>>> snapshot)
    {
        var root = new Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>();

        foreach (var tsEntry in snapshot)
        {
            var deploymentDict =
                new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.Ordinal);

            foreach (var deploymentEntry in tsEntry.Value)
            {
                var podDict = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

                foreach (var podEntry in deploymentEntry.Value)
                {
                    var metricsDict = new Dictionary<string, double>(podEntry.Value, StringComparer.Ordinal);
                    podDict[podEntry.Key] = metricsDict;
                }

                deploymentDict[deploymentEntry.Key] = podDict;
            }

            root[tsEntry.Key] = deploymentDict;
        }

        return new MetricsStoreRecord(root);
    }
}

public readonly record struct MetricPoint(long Timestamp, double Value);

public delegate void MetricsSeriesVisitor(
    int seriesId,
    string deployment,
    string podIp,
    string metricKey,
    IReadOnlyList<MetricPoint> points);

public interface IMetricsStore
{
    long? LatestTimestamp { get; }

    int SeriesCount { get; }

    void Add(long timestamp, string deployment, string podIp, IReadOnlyDictionary<string, double> metrics);

    IReadOnlyDictionary<long,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Snapshot();

    MetricsStoreRecord CreateRecord();

    void ReplaceFromRecord(MetricsStoreRecord record);

    void VisitSeries(MetricsSeriesVisitor visitor);
}

public sealed class InMemoryMetricsStore : IMetricsStore
{
    public const int DefaultMaxSeries = 10_000;
    public const int DefaultMaxPointsPerSeries = 361;

    private readonly object _sync = new();
    private readonly long _retentionSeconds;
    private readonly int _maxSeries;
    private readonly int _maxPointsPerSeries;
    private readonly IRequestedMetricsRegistry _registry;
    private StoreState _state = new();

    public InMemoryMetricsStore(
        IRequestedMetricsRegistry registry,
        long retentionSeconds = 1800,
        int maxSeries = DefaultMaxSeries,
        int maxPointsPerSeries = DefaultMaxPointsPerSeries)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSeries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPointsPerSeries);

        _registry = registry;
        _retentionSeconds = retentionSeconds;
        _maxSeries = maxSeries;
        _maxPointsPerSeries = maxPointsPerSeries;
    }

    public long? LatestTimestamp
    {
        get
        {
            lock (_sync)
                return _state.LatestTimestamp;
        }
    }

    public int SeriesCount
    {
        get
        {
            lock (_sync)
                return _state.SeriesById.Count;
        }
    }

    public void Add(
        long timestamp,
        string deployment,
        string podIp,
        IReadOnlyDictionary<string, double> metrics)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(podIp);
        ArgumentNullException.ThrowIfNull(metrics);

        lock (_sync)
        {
            var state = _state;
            AdvanceRetentionWindow(state, timestamp);
            var cutoff = state.LatestTimestamp.GetValueOrDefault(timestamp) - _retentionSeconds;
            if (timestamp < cutoff)
                return;

            foreach (var (metricKey, value) in metrics)
            {
                if (!_registry.IsRequestedKey(metricKey))
                    continue;

                var identity = new SeriesIdentity(deployment, podIp, metricKey);
                if (!state.SeriesIds.TryGetValue(identity, out var seriesId))
                {
                    if (state.SeriesById.Count >= _maxSeries)
                        continue;

                    seriesId = state.NextSeriesId++;
                    var created = new StoredSeries(
                        seriesId,
                        deployment,
                        podIp,
                        metricKey,
                        _maxPointsPerSeries);
                    state.SeriesIds.Add(identity, seriesId);
                    state.SeriesById.Add(seriesId, created);
                }

                state.SeriesById[seriesId].Points.Add(timestamp, value, cutoff);
            }
        }
    }

    public IReadOnlyDictionary<long,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Snapshot()
    {
        lock (_sync)
        {
            var root = new Dictionary<long,
                IReadOnlyDictionary<string,
                    IReadOnlyDictionary<string,
                        IReadOnlyDictionary<string, double>>>>();
            Populate(
                _state,
                (timestamp, deployment, podIp, metricKey, value) =>
                {
                    if (!root.TryGetValue(timestamp, out var readOnlyDeployments))
                    {
                        readOnlyDeployments =
                            new Dictionary<string,
                                IReadOnlyDictionary<string,
                                    IReadOnlyDictionary<string, double>>>(StringComparer.Ordinal);
                        root[timestamp] = readOnlyDeployments;
                    }

                    var deployments =
                        (Dictionary<string,
                            IReadOnlyDictionary<string,
                                IReadOnlyDictionary<string, double>>>)readOnlyDeployments;
                    if (!deployments.TryGetValue(deployment, out var readOnlyPods))
                    {
                        readOnlyPods =
                            new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);
                        deployments[deployment] = readOnlyPods;
                    }

                    var pods = (Dictionary<string, IReadOnlyDictionary<string, double>>)readOnlyPods;
                    if (!pods.TryGetValue(podIp, out var readOnlyMetrics))
                    {
                        readOnlyMetrics = new Dictionary<string, double>(StringComparer.Ordinal);
                        pods[podIp] = readOnlyMetrics;
                    }

                    ((Dictionary<string, double>)readOnlyMetrics)[metricKey] = value;
                });
            return root;
        }
    }

    public MetricsStoreRecord CreateRecord()
    {
        lock (_sync)
        {
            var root =
                new Dictionary<long, Dictionary<string, Dictionary<string, Dictionary<string, double>>>>();
            Populate(
                _state,
                (timestamp, deployment, podIp, metricKey, value) =>
                {
                    if (!root.TryGetValue(timestamp, out var deployments))
                    {
                        deployments =
                            new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(
                                StringComparer.Ordinal);
                        root[timestamp] = deployments;
                    }

                    if (!deployments.TryGetValue(deployment, out var pods))
                    {
                        pods = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
                        deployments[deployment] = pods;
                    }

                    if (!pods.TryGetValue(podIp, out var metrics))
                    {
                        metrics = new Dictionary<string, double>(StringComparer.Ordinal);
                        pods[podIp] = metrics;
                    }

                    metrics[metricKey] = value;
                });
            return new MetricsStoreRecord(root);
        }
    }

    public void ReplaceFromRecord(MetricsStoreRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(record.Store);

        var replacement = new StoreState();
        if (record.Store.Count > 0)
        {
            var latestTimestamp = record.Store.Keys.Max();
            var cutoff = latestTimestamp - _retentionSeconds;
            replacement.LatestTimestamp = latestTimestamp;

            foreach (var (timestamp, deployments) in record.Store.OrderBy(entry => entry.Key))
            {
                if (timestamp < cutoff)
                    continue;

                foreach (var (deployment, pods) in deployments)
                {
                    foreach (var (podIp, metrics) in pods)
                    {
                        foreach (var (metricKey, value) in metrics)
                        {
                            var identity = new SeriesIdentity(deployment, podIp, metricKey);
                            if (!replacement.SeriesIds.TryGetValue(identity, out var seriesId))
                            {
                                if (replacement.SeriesById.Count >= _maxSeries)
                                    continue;

                                seriesId = replacement.NextSeriesId++;
                                var created = new StoredSeries(
                                    seriesId,
                                    deployment,
                                    podIp,
                                    metricKey,
                                    _maxPointsPerSeries);
                                replacement.SeriesIds.Add(identity, seriesId);
                                replacement.SeriesById.Add(seriesId, created);
                            }

                            replacement.SeriesById[seriesId].Points.Add(timestamp, value, cutoff);
                        }
                    }
                }
            }
        }

        lock (_sync)
            _state = replacement;
    }

    public void VisitSeries(MetricsSeriesVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);

        lock (_sync)
        {
            foreach (var series in _state.SeriesById.Values)
            {
                visitor(
                    series.Id,
                    series.Deployment,
                    series.PodIp,
                    series.MetricKey,
                    series.Points);
            }
        }
    }

    private void AdvanceRetentionWindow(StoreState state, long timestamp)
    {
        if (state.LatestTimestamp is not { } latest || timestamp > latest)
        {
            state.LatestTimestamp = timestamp;
            var cutoff = timestamp - _retentionSeconds;
            List<int>? emptySeries = null;

            foreach (var (seriesId, series) in state.SeriesById)
            {
                series.Points.TrimBefore(cutoff);
                if (series.Points.Count == 0)
                {
                    emptySeries ??= [];
                    emptySeries.Add(seriesId);
                }
            }

            if (emptySeries is null)
                return;

            foreach (var seriesId in emptySeries)
            {
                var series = state.SeriesById[seriesId];
                state.SeriesById.Remove(seriesId);
                state.SeriesIds.Remove(
                    new SeriesIdentity(series.Deployment, series.PodIp, series.MetricKey));
            }
        }
    }

    private static void Populate(
        StoreState state,
        Action<long, string, string, string, double> add)
    {
        foreach (var series in state.SeriesById.Values)
        {
            for (var index = 0; index < series.Points.Count; index++)
            {
                var point = series.Points[index];
                add(
                    point.Timestamp,
                    series.Deployment,
                    series.PodIp,
                    series.MetricKey,
                    point.Value);
            }
        }
    }

    private readonly record struct SeriesIdentity(string Deployment, string PodIp, string MetricKey);

    private sealed class StoreState
    {
        public Dictionary<SeriesIdentity, int> SeriesIds { get; } = new();

        public Dictionary<int, StoredSeries> SeriesById { get; } = new();

        public int NextSeriesId { get; set; }

        public long? LatestTimestamp { get; set; }
    }

    private sealed class StoredSeries(
        int id,
        string deployment,
        string podIp,
        string metricKey,
        int maxPoints)
    {
        public int Id { get; } = id;

        public string Deployment { get; } = deployment;

        public string PodIp { get; } = podIp;

        public string MetricKey { get; } = metricKey;

        public SeriesPointBuffer Points { get; } = new(maxPoints);
    }

    private sealed class SeriesPointBuffer(int maxPoints) : IReadOnlyList<MetricPoint>
    {
        private MetricPoint[] _items = [];
        private int _head;

        public int Count { get; private set; }

        public MetricPoint this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                if (index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _items[PhysicalIndex(index)];
            }
        }

        public void Add(long timestamp, double value, long cutoff)
        {
            TrimBefore(cutoff);
            if (timestamp < cutoff)
                return;

            if (Count > 0)
            {
                var lastIndex = PhysicalIndex(Count - 1);
                var last = _items[lastIndex];
                if (timestamp == last.Timestamp)
                {
                    _items[lastIndex] = new MetricPoint(timestamp, value);
                    return;
                }

                if (timestamp < last.Timestamp)
                {
                    AddOutOfOrder(timestamp, value);
                    return;
                }
            }

            if (Count == maxPoints)
            {
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
                Count--;
            }

            EnsureCapacity();
            _items[PhysicalIndex(Count)] = new MetricPoint(timestamp, value);
            Count++;
        }

        public void TrimBefore(long cutoff)
        {
            while (Count > 0 && _items[_head].Timestamp < cutoff)
            {
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
                Count--;
            }

            if (Count == 0)
                _head = 0;
        }

        public IEnumerator<MetricPoint> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private int PhysicalIndex(int logicalIndex)
            => _items.Length == 0 ? 0 : (_head + logicalIndex) % _items.Length;

        private void EnsureCapacity()
        {
            if (Count < _items.Length)
                return;

            var requested = _items.Length == 0 ? 4 : _items.Length * 2;
            var newCapacity = Math.Min(maxPoints, requested);
            if (newCapacity <= _items.Length)
                return;

            var replacement = new MetricPoint[newCapacity];
            for (var index = 0; index < Count; index++)
                replacement[index] = this[index];
            _items = replacement;
            _head = 0;
        }

        private void AddOutOfOrder(long timestamp, double value)
        {
            var points = new List<MetricPoint>(Count + 1);
            for (var index = 0; index < Count; index++)
                points.Add(this[index]);

            var insertionIndex = points.BinarySearch(
                new MetricPoint(timestamp, value),
                MetricPointTimestampComparer.Instance);
            if (insertionIndex >= 0)
                points[insertionIndex] = new MetricPoint(timestamp, value);
            else
                points.Insert(~insertionIndex, new MetricPoint(timestamp, value));

            var retained = Math.Min(points.Count, maxPoints);
            var firstRetained = points.Count - retained;
            _items = new MetricPoint[Math.Max(4, retained)];
            _head = 0;
            Count = retained;
            for (var index = 0; index < retained; index++)
                _items[index] = points[firstRetained + index];
        }
    }

    private sealed class MetricPointTimestampComparer : IComparer<MetricPoint>
    {
        public static MetricPointTimestampComparer Instance { get; } = new();

        public int Compare(MetricPoint x, MetricPoint y) => x.Timestamp.CompareTo(y.Timestamp);
    }
}
