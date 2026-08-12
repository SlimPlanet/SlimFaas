namespace SlimFaas.Kubernetes.Watch;

/// <summary>
/// Process-local change signal for a watched Kubernetes resource group. Same
/// mechanics as SlimDataQueueSignal: consumers keep their own observed version, so a
/// wake-up is never consumed by another worker, and a pulse that happens while a
/// consumer is busy is observed on its next wait (no lost wake-ups).
/// </summary>
public sealed class KubernetesResourceSignal(TimeProvider? timeProvider = null)
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

    /// <summary>
    /// Waits until the version advances past <paramref name="observedVersion"/> or
    /// <paramref name="maximumWait"/> elapses (timeout is not an error), and returns
    /// the current version. Returns immediately if the version already advanced.
    /// </summary>
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
