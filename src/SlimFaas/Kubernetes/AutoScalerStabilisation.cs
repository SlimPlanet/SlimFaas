using System.Collections.Concurrent;

namespace SlimFaas.Kubernetes;

public interface IAutoScalerStore
{
    /// <summary>
    /// Ajoute un échantillon de "desired replicas" pour un déploiement donné.
    /// </summary>
    void AddSample(string key, long timestampUnixSeconds, int desiredReplicas);

    /// <summary>Records a replica change, including the count before the change.</summary>
    void AddSample(string key, long timestampUnixSeconds, int desiredReplicas, int previousReplicas);

    /// <summary>
    /// Records a replica change and whether it was a wake-up (a count applied because of HTTP,
    /// schedule or dependency activity rather than produced by the metric scaling policies).
    /// A wake-up never consumes the scale-up policy budget (issue #370).
    /// </summary>
    void AddSample(string key, long timestampUnixSeconds, int desiredReplicas, int previousReplicas, bool wakeUp)
        => AddSample(key, timestampUnixSeconds, desiredReplicas, previousReplicas);

    /// <summary>
    /// Récupère tous les échantillons >= fromTimestampUnixSeconds pour un déploiement.
    /// </summary>
    IReadOnlyList<AutoScaleSample> GetSamples(string key, long fromTimestampUnixSeconds);
}

public readonly record struct AutoScaleSample
{
    public long TimestampUnixSeconds { get; }
    public int DesiredReplicas { get; }
    public int? PreviousReplicas { get; }

    /// <summary>
    /// True when the change was a wake-up (HTTP, schedule, dependency or external-signal
    /// activity applied a count the metric policies did not produce). Such a change does
    /// not consume the scale-up policy budget.
    /// </summary>
    public bool WakeUp { get; }

    public AutoScaleSample(long timestampUnixSeconds, int desiredReplicas, int? previousReplicas = null, bool wakeUp = false)
    {
        TimestampUnixSeconds = timestampUnixSeconds;
        DesiredReplicas = desiredReplicas;
        PreviousReplicas = previousReplicas;
        WakeUp = wakeUp;
    }
}

public sealed class InMemoryAutoScalerStore : IAutoScalerStore
{
    private readonly ConcurrentDictionary<string, List<AutoScaleSample>> _samples = new(StringComparer.Ordinal);
    private readonly int _maxSamplesPerKey;

    public InMemoryAutoScalerStore(int maxSamplesPerKey = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSamplesPerKey);
        _maxSamplesPerKey = maxSamplesPerKey;
    }

    public void AddSample(string key, long timestampUnixSeconds, int desiredReplicas)
        => AddSample(key, new AutoScaleSample(timestampUnixSeconds, desiredReplicas));

    public void AddSample(string key, long timestampUnixSeconds, int desiredReplicas, int previousReplicas)
        => AddSample(key, new AutoScaleSample(timestampUnixSeconds, desiredReplicas, previousReplicas));

    public void AddSample(string key, long timestampUnixSeconds, int desiredReplicas, int previousReplicas, bool wakeUp)
        => AddSample(key, new AutoScaleSample(timestampUnixSeconds, desiredReplicas, previousReplicas, wakeUp));

    private void AddSample(string key, AutoScaleSample sample)
    {
        var list = _samples.GetOrAdd(key, _ => new List<AutoScaleSample>());
        lock (list)
        {
            list.Add(sample);
            var overflow = list.Count - _maxSamplesPerKey;
            if (overflow > 0)
            {
                list.RemoveRange(0, overflow);
            }
        }
    }

    public IReadOnlyList<AutoScaleSample> GetSamples(string key, long fromTimestampUnixSeconds)
    {
        if (!_samples.TryGetValue(key, out var list))
            return Array.Empty<AutoScaleSample>();

        lock (list)
        {
            if (list.Count == 0)
                return Array.Empty<AutoScaleSample>();

            // Liste triée par temps, on cherche le premier >= fromTimestampUnixSeconds
            var start = 0;
            while (start < list.Count && list[start].TimestampUnixSeconds < fromTimestampUnixSeconds)
                start++;

            if (start >= list.Count)
                return Array.Empty<AutoScaleSample>();

            return list.GetRange(start, list.Count - start).ToArray();
        }
    }
}
