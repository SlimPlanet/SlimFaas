using System.Collections.Concurrent;

namespace SlimFaas.Security;

/// <summary>
/// Lets a message through at most once per <paramref name="interval"/> for a given key,
/// so that warnings driven by request traffic cannot flood the log. Bounded: the table is
/// cleared when it exceeds <paramref name="maxKeys"/> entries.
/// </summary>
internal sealed class LogThrottle(TimeSpan interval, int maxKeys = 10_000, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, long> _lastLogged = new(StringComparer.Ordinal);

    public bool ShouldLog(string key)
    {
        long now = _timeProvider.GetTimestamp();
        if (_lastLogged.Count > maxKeys)
        {
            _lastLogged.Clear();
        }

        while (true)
        {
            if (!_lastLogged.TryGetValue(key, out long last))
            {
                if (_lastLogged.TryAdd(key, now))
                {
                    return true;
                }

                continue;
            }

            if (_timeProvider.GetElapsedTime(last, now) < interval)
            {
                return false;
            }

            if (_lastLogged.TryUpdate(key, now, last))
            {
                return true;
            }
        }
    }
}
