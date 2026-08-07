namespace SlimData;

/// <summary>
/// Process-local broadcast signal emitted after a durable queue mutation is committed.
/// Consumers retain their own observed version, so a wake-up is never consumed by another worker.
/// </summary>
public sealed class SlimDataQueueSignal(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TaskCompletionSource _changed = CreateCompletion();
    private long _version;

    public long Version => Volatile.Read(ref _version);

    public void Pulse()
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            Interlocked.Increment(ref _version);
            changed = _changed;
            _changed = CreateCompletion();
        }
        changed.TrySetResult();
    }

    public async Task<long> WaitForChangeAsync(
        long observedVersion,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        Task changed;
        lock (_gate)
        {
            if (_version != observedVersion)
                return _version;
            changed = _changed.Task;
        }

        try
        {
            await changed.WaitAsync(maximumWait, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        return Version;
    }

    private static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
