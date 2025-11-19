using System.Collections.Concurrent;
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
            var deploymentDict = new Dictionary<string, Dictionary<string, Dictionary<string, double>>>(StringComparer.Ordinal);

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


public interface IMetricsStore
{
    void Add(long timestamp, string deployment, string podIp, IReadOnlyDictionary<string, double> metrics);

    public IReadOnlyDictionary<long,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Snapshot();

    void ReplaceFromRecord(MetricsStoreRecord record);

}

public class InMemoryMetricsStore : IMetricsStore
{
    private ConcurrentDictionary<long,
        ConcurrentDictionary<string,
            ConcurrentDictionary<string,
                ConcurrentDictionary<string, double>>>> _store = new();

    private readonly long _retentionSeconds;
    private readonly IRequestedMetricsRegistry _registry;

    public InMemoryMetricsStore(IRequestedMetricsRegistry registry, long retentionSeconds = 1800)
    {
        _registry = registry;
        _retentionSeconds = retentionSeconds;
    }

    public void Add(long timestamp, string deployment, string podIp, IReadOnlyDictionary<string, double> metrics)
    {
        // 1) Nettoyage par rétention
        var minAllowed = timestamp - _retentionSeconds;
        foreach (var key in _store.Keys)
        {
            if (key < minAllowed)
                _store.TryRemove(key, out _);
        }

        // 2) Filtre sur les métriques “demandées”
        var any = false;

        var d = _store.GetOrAdd(timestamp, _ => new());
        var dd = d.GetOrAdd(deployment, _ => new());
        var p = dd.GetOrAdd(podIp, _ => new());

        foreach (var kv in metrics)
        {
            if (!_registry.IsRequestedKey(kv.Key))
                continue;

            p[kv.Key] = kv.Value;
            any = true;
        }

        // Si rien d'intéressant, on peut laisser les structures vides,
        // mais on pourrait aussi nettoyer dd/p si besoin.
        if (!any)
        {
            if (p.Count == 0 && dd.TryGetValue(podIp, out _))
                dd.TryRemove(podIp, out _);
        }
    }

    public IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Snapshot()
    {
        return _store.ToDictionary(
            t => t.Key,
            t => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>)t.Value.ToDictionary(
                d => d.Key,
                d => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>)d.Value.ToDictionary(
                    p => p.Key,
                    p => (IReadOnlyDictionary<string, double>)p.Value.ToDictionary(m => m.Key, m => m.Value)
                )
            )
        );
    }

    public void ReplaceFromRecord(MetricsStoreRecord record)
    {
        var newStore = new ConcurrentDictionary<long,
            ConcurrentDictionary<string,
                ConcurrentDictionary<string,
                    ConcurrentDictionary<string, double>>>>();

        foreach (var tsEntry in record.Store)
        {
            var deploymentDict = new ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<string, double>>>(StringComparer.Ordinal);

            foreach (var deploymentEntry in tsEntry.Value)
            {
                var podDict = new ConcurrentDictionary<string, ConcurrentDictionary<string, double>>(StringComparer.Ordinal);

                foreach (var podEntry in deploymentEntry.Value)
                {
                    var metricsDict = new ConcurrentDictionary<string, double>(podEntry.Value, StringComparer.Ordinal);
                    podDict[podEntry.Key] = metricsDict;
                }

                deploymentDict[deploymentEntry.Key] = podDict;
            }

            newStore[tsEntry.Key] = deploymentDict;
        }

        // 🔁 Switch atomique de la référence
        Interlocked.Exchange(ref _store, newStore);
    }

}
