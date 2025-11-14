using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SlimFaas.Scaling;

public interface IAutoScalerStore
{
    /// <summary>
    /// Ajoute un échantillon de "desired replicas" pour un déploiement donné.
    /// </summary>
    void AddSample(string key, long timestampUnixSeconds, int desiredReplicas);

    /// <summary>
    /// Récupère tous les échantillons >= fromTimestampUnixSeconds pour un déploiement.
    /// </summary>
    IReadOnlyList<AutoScaleSample> GetSamples(string key, long fromTimestampUnixSeconds);
}

public readonly struct AutoScaleSample
{
    public long TimestampUnixSeconds { get; }
    public int DesiredReplicas { get; }

    public AutoScaleSample(long timestampUnixSeconds, int desiredReplicas)
    {
        TimestampUnixSeconds = timestampUnixSeconds;
        DesiredReplicas = desiredReplicas;
    }
}

public sealed class InMemoryAutoScalerStore : IAutoScalerStore
{
    private readonly ConcurrentDictionary<string, List<AutoScaleSample>> _samples = new(StringComparer.Ordinal);
    private readonly int _maxSamplesPerKey;

    public InMemoryAutoScalerStore(int maxSamplesPerKey = 1024)
    {
        if (maxSamplesPerKey <= 0) throw new ArgumentOutOfRangeException(nameof(maxSamplesPerKey));
        _maxSamplesPerKey = maxSamplesPerKey;
    }

    public void AddSample(string key, long timestampUnixSeconds, int desiredReplicas)
    {
        var list = _samples.GetOrAdd(key, _ => new List<AutoScaleSample>());
        lock (list)
        {
            list.Add(new AutoScaleSample(timestampUnixSeconds, desiredReplicas));
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
