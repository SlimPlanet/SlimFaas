namespace SlimFaas.Options;

/// <summary>
/// Options for the Kubernetes watch-driven synchronization. When enabled (and the
/// orchestrator is Kubernetes), watch streams on pods/deployments/statefulsets/jobs/
/// cronjobs turn synchronization into an event-driven process: the existing LIST-based
/// syncs only run when something changed, with a periodic resync as a safety net.
/// Disabling it (or running a non-Kubernetes orchestrator) restores the legacy
/// fixed-cadence polling exactly.
/// </summary>
public class KubernetesWatchOptions
{
    /// <summary>Enable watch-driven synchronization (Kubernetes orchestrator only)</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Safety-net resync interval for functions (deployments/pods/statefulsets)</summary>
    public int FunctionsResyncSeconds { get; set; } = 30;

    /// <summary>Safety-net resync interval for jobs</summary>
    public int JobsResyncSeconds { get; set; } = 30;

    /// <summary>Safety-net resync interval for the CronJob-based jobs configuration</summary>
    public int JobsConfigurationResyncSeconds { get; set; } = 60;

    /// <summary>Debounce window applied to bursts of watch events before signaling</summary>
    public int DebounceMilliseconds { get; set; } = 300;

    /// <summary>Server-side watch timeout (stream rotation); must stay below the HttpClient timeout (100 s)</summary>
    public int WatchTimeoutSeconds { get; set; } = 60;

    /// <summary>Initial reconnect delay after a watch stream failure</summary>
    public int ReconnectInitialDelayMilliseconds { get; set; } = 1000;

    /// <summary>Maximum reconnect delay (exponential backoff cap)</summary>
    public int ReconnectMaxDelayMilliseconds { get; set; } = 30000;
}
