using System.Collections.Concurrent;

namespace SlimFaas.Security;

/// <summary>
/// Remembers the nonces already accepted for each caller during the replay window.
/// Bounded per caller: when a caller's cache is full and no entry has expired, its
/// requests are refused (fail closed) until entries expire. Only signatures that
/// verified are recorded, so an attacker without the key cannot fill the cache.
/// </summary>
internal sealed class NonceCache(TimeSpan window, int maxEntriesPerCaller, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, CallerNonces> _callers = new(StringComparer.Ordinal);

    /// <summary>Returns <c>true</c> when the nonce was not seen before for the caller and is now recorded.</summary>
    public bool TryAdd(string callerId, string nonce)
    {
        CallerNonces nonces = _callers.GetOrAdd(callerId, static _ => new CallerNonces());
        long now = _timeProvider.GetTimestamp();
        long expiresAt = now + (long)(window.TotalSeconds * _timeProvider.TimestampFrequency);
        return nonces.TryAdd(nonce, now, expiresAt, maxEntriesPerCaller);
    }

    private sealed class CallerNonces
    {
        private readonly Dictionary<string, long> _expirations = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public bool TryAdd(string nonce, long now, long expiresAt, int maxEntries)
        {
            lock (_gate)
            {
                if (_expirations.TryGetValue(nonce, out long existing))
                {
                    if (existing > now)
                    {
                        return false;
                    }

                    _expirations[nonce] = expiresAt;
                    return true;
                }

                if (_expirations.Count >= maxEntries)
                {
                    Purge(now);
                    if (_expirations.Count >= maxEntries)
                    {
                        return false;
                    }
                }

                _expirations[nonce] = expiresAt;
                return true;
            }
        }

        private void Purge(long now)
        {
            List<string> expired = [];
            foreach (KeyValuePair<string, long> pair in _expirations)
            {
                if (pair.Value <= now)
                {
                    expired.Add(pair.Key);
                }
            }

            foreach (string nonce in expired)
            {
                _expirations.Remove(nonce);
            }
        }
    }
}
