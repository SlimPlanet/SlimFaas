using System.Diagnostics;

namespace SlimFaas.Workers;

/// <summary>Tracks a stationary applied index only while the local WAL has pending entries.</summary>
internal sealed class SlimDataProgressTracker
{
    internal const int StallThresholdSeconds = 30;
    private long? _pendingSince;

    internal bool IsStalled { get; private set; }

    /// <returns>Whether the stall indication changed on this observation.</returns>
    internal bool Observe(long lastIndex, long? appliedIndex, long previousAppliedIndex, bool logRewound, long now)
    {
        bool wasStalled = IsStalled;
        if (appliedIndex is null || lastIndex <= appliedIndex)
        {
            _pendingSince = null;
            IsStalled = false;
        }
        else
        {
            if (_pendingSince is null || appliedIndex != previousAppliedIndex || logRewound)
                _pendingSince = now;

            IsStalled = Stopwatch.GetElapsedTime(_pendingSince.Value, now).TotalSeconds >= StallThresholdSeconds;
        }

        return wasStalled != IsStalled;
    }
}
