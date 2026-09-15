namespace SlimFaas.Workers;

/// <summary>Tracks a stationary applied index only while the local WAL has pending entries.</summary>
internal sealed class SlimDataProgressTracker(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan StallThreshold = TimeSpan.FromSeconds(30);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long? _pendingSince;
    private long _previousLastIndex;
    private long? _previousAppliedIndex;

    internal bool IsStalled { get; private set; }

    /// <returns>Whether the stall indication changed on this observation.</returns>
    internal bool Observe(long lastIndex, long? appliedIndex)
    {
        var wasStalled = IsStalled;
        var now = _timeProvider.GetTimestamp();
        if (appliedIndex is null or < 0 || lastIndex <= appliedIndex)
        {
            _pendingSince = null;
            IsStalled = false;
        }
        else
        {
            if (_pendingSince is null || appliedIndex != _previousAppliedIndex || lastIndex < _previousLastIndex)
                _pendingSince = now;

            IsStalled = _timeProvider.GetElapsedTime(_pendingSince.Value, now) >= StallThreshold;
        }

        _previousLastIndex = lastIndex;
        _previousAppliedIndex = appliedIndex;
        return wasStalled != IsStalled;
    }
}
