namespace SlimFaas.Kubernetes.Watch;

/// <summary>
/// The three logical change channels fed by the Kubernetes watch streams:
/// - Functions: deployments + statefulsets + function pods (ReplicasSynchronizationWorker)
/// - Jobs: jobs + job pods (SlimJobsWorker)
/// - JobsConfiguration: cronjobs (SlimJobsConfigurationWorker)
/// <see cref="WatchEnabled"/> is set once at registration time; when false, no pulse
/// ever fires and consumers fall back to their legacy polling cadence. When set to
/// true, each signal starts with a connection debt for the streams expected to feed
/// it: the signals stay unhealthy — and the consumers on their legacy cadence — until
/// the watcher actually connects the streams, so a watcher that never starts (early
/// startup exception, registration drift) silently degrades to legacy polling instead
/// of the slow safety-net resync.
/// </summary>
public sealed class KubernetesWatchSignals
{
    public KubernetesResourceSignal Functions { get; } = new();
    public KubernetesResourceSignal Jobs { get; } = new();
    public KubernetesResourceSignal JobsConfiguration { get; } = new();

    private bool _watchEnabled;

    public bool WatchEnabled
    {
        get => _watchEnabled;
        set
        {
            if (value && !_watchEnabled)
            {
                // Doit refléter la topologie des WatchTarget de KubernetesWatcherWorker :
                // Functions ← pods de fonctions + deployments + statefulsets,
                // Jobs ← jobs + pods de jobs, JobsConfiguration ← cronjobs.
                Functions.ExpectStream();
                Functions.ExpectStream();
                Functions.ExpectStream();
                Jobs.ExpectStream();
                Jobs.ExpectStream();
                JobsConfiguration.ExpectStream();
            }

            _watchEnabled = value;
        }
    }

    /// <summary>
    /// Simule des flux watch tous connectés — réservé aux tests qui veulent observer
    /// le comportement event-driven sans faire tourner le watcher.
    /// </summary>
    internal void MarkAllStreamsConnected()
    {
        while (!Functions.IsHealthy)
        {
            Functions.ReportStreamUp();
        }

        while (!Jobs.IsHealthy)
        {
            Jobs.ReportStreamUp();
        }

        while (!JobsConfiguration.IsHealthy)
        {
            JobsConfiguration.ReportStreamUp();
        }
    }
}
