namespace SlimFaas.Database;

/// <summary>Shares a warning budget across concurrent readiness waiters.</summary>
internal sealed class SlimDataReadinessLogLimiter(TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan ReminderInterval = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _reason;
    private long _lastWarning;

    internal bool ShouldLog(string reason)
    {
        lock (_gate)
        {
            long now = _clock.GetTimestamp();
            if (string.Equals(_reason, reason, StringComparison.Ordinal) &&
                _clock.GetElapsedTime(_lastWarning, now) < ReminderInterval)
                return false;

            _reason = reason;
            _lastWarning = now;
            return true;
        }
    }

    internal void Reset()
    {
        lock (_gate)
            _reason = null;
    }
}
