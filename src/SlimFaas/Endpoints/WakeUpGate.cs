using System.Collections.Concurrent;

namespace SlimFaas.Endpoints;

public sealed class WakeUpGate
{
    // Prochain instant (ticks UTC) où un wake est autorisé
    private readonly ConcurrentDictionary<string, long> _nextAllowedTicks = new(StringComparer.Ordinal);

    // Empêche les wakes concurrents
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.Ordinal);

    private readonly long _cooldownTicks;

    public WakeUpGate(TimeSpan? cooldown = null)
    {
        _cooldownTicks = (cooldown ?? TimeSpan.FromSeconds(3)).Ticks; // ✅ 3 secondes
    }

    /// <summary>
    /// Retourne true si on autorise le wake maintenant (et donc on peut lancer le fire-and-forget),
    /// false sinon (pas de Task créée).
    /// </summary>
    public bool TryEnter(string functionName)
    {
        var now = DateTime.UtcNow.Ticks;

        var nextAllowed = _nextAllowedTicks.GetOrAdd(functionName, 0);
        if (now < nextAllowed)
            return false; // ✅ wake trop récent (< 3s), on ne lance rien

        if (!_inflight.TryAdd(functionName, 0))
            return false; // ✅ un wake est déjà en cours, on ne lance rien

        _nextAllowedTicks[functionName] = now + _cooldownTicks;
        return true;
    }

    public void Exit(string functionName)
    {
        _inflight.TryRemove(functionName, out _);
    }
}
