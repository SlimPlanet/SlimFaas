namespace SlimFaas.Kubernetes.Watch;

/// <summary>
/// The three logical change channels fed by the Kubernetes watch streams:
/// - Functions: deployments + statefulsets + pods (ReplicasSynchronizationWorker)
/// - Jobs: jobs + pods (SlimJobsWorker)
/// - JobsConfiguration: cronjobs (SlimJobsConfigurationWorker)
/// <see cref="WatchEnabled"/> is set once at registration time; when false, no pulse
/// ever fires and consumers fall back to their legacy polling cadence.
/// </summary>
public sealed class KubernetesWatchSignals
{
    public KubernetesResourceSignal Functions { get; } = new();
    public KubernetesResourceSignal Jobs { get; } = new();
    public KubernetesResourceSignal JobsConfiguration { get; } = new();
    public bool WatchEnabled { get; set; }
}
