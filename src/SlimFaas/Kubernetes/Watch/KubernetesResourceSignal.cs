namespace SlimFaas.Kubernetes.Watch;

/// <summary>
/// Process-local change signal for a watched Kubernetes resource group. Same
/// mechanics as SlimDataQueueSignal: consumers keep their own observed version, so a
/// wake-up is never consumed by another worker, and a pulse that happens while a
/// consumer is busy is observed on its next wait (no lost wake-ups).
/// The signal also tracks the health of the watch streams feeding it: while at least
/// one stream is down (RBAC denial, API server unreachable, ...), consumers must not
/// rely on events and fall back to their legacy polling cadence.
/// </summary>
public sealed class KubernetesResourceSignal(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TaskCompletionSource _changed = CreateCompletion();
    private long _version;
    private int _unhealthyStreams;

    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// True when every watch stream feeding this signal is connected. False while at
    /// least one stream is down — or, quand les flux attendus ont été déclarés via
    /// <see cref="ExpectStream"/>, tant qu'ils ne se sont pas tous connectés : events
    /// may be missed, so consumers should poll at their legacy cadence.
    /// </summary>
    public bool IsHealthy => Volatile.Read(ref _unhealthyStreams) == 0;

    /// <summary>
    /// Déclare qu'un flux watch alimentera ce signal : le signal reste indisponible
    /// (les consommateurs gardent leur cadence historique) tant que ce flux n'a pas
    /// signalé sa connexion via <see cref="ReportStreamUp"/>. Appelé à l'activation du
    /// watch, avant le démarrage des hosted services — « watcher jamais démarré »
    /// (exception au démarrage, mauvais orchestrateur) est ainsi indistinguable d'un
    /// flux en panne, jamais d'un flux sain.
    /// </summary>
    public void ExpectStream() => Interlocked.Increment(ref _unhealthyStreams);

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
    /// Reports that one of the streams feeding this signal is down. Pulses so that a
    /// consumer waiting on the (long) resync timeout wakes up and re-evaluates its
    /// cadence immediately.
    /// </summary>
    public void ReportStreamDown()
    {
        Interlocked.Increment(ref _unhealthyStreams);
        Pulse();
    }

    /// <summary>
    /// Reports that a stream is connected. Must be paired with exactly one
    /// <see cref="ReportStreamDown"/> or <see cref="ExpectStream"/>.
    /// </summary>
    public void ReportStreamUp()
    {
        Interlocked.Decrement(ref _unhealthyStreams);
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
            // Timeout is the periodic resync path, not an error: return the current version.
        }
        return Version;
    }

    private static TaskCompletionSource CreateCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
