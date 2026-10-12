namespace SlimFaas.Workers;

/// <summary>Tracks consensus loss independently of local WAL progress.</summary>
internal sealed class SlimDataAvailabilityTracker(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private long? _unavailableSince;
    private long _lastNotification;
    private (bool HasLeader, bool HasConsensus) _previous;

    internal double UnavailableSeconds { get; private set; }

    internal AvailabilityChange Observe(bool hasLeader, bool hasConsensus)
    {
        if (hasLeader && hasConsensus)
        {
            bool recovered = _unavailableSince.HasValue;
            _unavailableSince = null;
            UnavailableSeconds = 0;
            return recovered ? AvailabilityChange.Recovered : AvailabilityChange.None;
        }

        long now = _clock.GetTimestamp();
        bool changed = _unavailableSince is null || _previous != (hasLeader, hasConsensus);
        _unavailableSince ??= now;
        UnavailableSeconds = _clock.GetElapsedTime(_unavailableSince.Value, now).TotalSeconds;
        _previous = (hasLeader, hasConsensus);
        if (changed || _clock.GetElapsedTime(_lastNotification, now) >= ReminderInterval)
        {
            _lastNotification = now;
            return AvailabilityChange.Unavailable;
        }

        return AvailabilityChange.None;
    }

    internal enum AvailabilityChange
    {
        None,
        Unavailable,
        Recovered
    }
}
