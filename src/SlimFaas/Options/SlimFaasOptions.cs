using SlimFaas.RateLimiting;

namespace SlimFaas.Options;

/// <summary>
/// Configuration options for SlimFaas
/// </summary>
public class SlimFaasOptions
{
    public const string SectionName = "SlimFaas";

    /// <summary>
    /// Allow unsecure SSL connections
    /// </summary>
    public bool AllowUnsecureSsl { get; set; }

    /// <summary>
    /// Jobs configuration in JSON format
    /// </summary>
    public string? JobsConfiguration { get; set; }

    /// <summary>
    /// CORS allowed origins (comma separated, or * for all)
    /// </summary>
    public string CorsAllowOrigin { get; set; } = "*";

    /// <summary>
    /// Base URL for SlimData service
    /// </summary>
    public string BaseSlimDataUrl { get; set; } = "http://{pod_name}.{service_name}.{namespace}.svc:3262";

    /// <summary>
    /// Base URL for function pods
    /// </summary>
    public string BaseFunctionUrl { get; set; } = "http://{pod_ip}:{pod_port}";

    /// <summary>
    /// Base URL for function pods (alternative)
    /// </summary>
    public string BaseFunctionPodUrl { get; set; } = "http://{pod_ip}:{pod_port}";

    /// <summary>
    /// Kubernetes namespace
    /// </summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Orchestrator type: Kubernetes, Docker, or Mock
    /// </summary>
    public string Orchestrator { get; set; } = "Kubernetes";

    /// <summary>
    /// Pod scaled up by default when infrastructure has never been called
    /// </summary>
    public bool PodScaledUpByDefaultWhenInfrastructureHasNeverCalled { get; set; }

    /// <summary>
    /// Rate limiting configuration
    /// </summary>
    public RateLimitingOptions RateLimiting { get; set; } = new();

    /// <summary>
    /// Port dédié aux connexions WebSocket des clients virtuels (jobs, etc.).
    /// 0 = désactivé.
    /// </summary>
    public int WebSocketPort { get; set; } = 5003;

    /// <summary>
    /// Enables the SlimFaas dashboard/network front features.
    /// </summary>
    public bool EnableFront { get; set; } = true;

    /// <summary>
    /// Typed configuration for the dashboard status stream and live network activity events.
    /// </summary>
    public StatusStreamOptions StatusStream { get; set; } = new();
}

public class StatusStreamOptions
{
    /// <summary>
    /// Interval between periodic SSE state snapshots.
    /// </summary>
    public int StateIntervalMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Cache duration for expensive queue length reads used by state snapshots.
    /// </summary>
    public int QueueLengthsCacheMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Cache duration for job status snapshots used by state snapshots.
    /// </summary>
    public int JobsCacheMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Interval between peer activity scrapes in multi-node deployments.
    /// </summary>
    public int PeerSyncIntervalMilliseconds { get; set; } = 2000;

    /// <summary>
    /// Initial delay before the first peer activity scrape.
    /// </summary>
    public int PeerSyncInitialDelayMilliseconds { get; set; } = 5000;

    /// <summary>
    /// Maximum concurrent SSE clients per SlimFaas pod. 0 means unlimited.
    /// </summary>
    public int MaxSseClients { get; set; }

    /// <summary>
    /// Bounded channel capacity per SSE subscriber for live activity events.
    /// </summary>
    public int SubscriberChannelCapacity { get; set; } = 10000;

    /// <summary>
    /// Maximum number of recent activity events retained in memory for snapshots and peer sync.
    /// </summary>
    public int RecentActivityLimit { get; set; } = 1000;

    /// <summary>
    /// Maximum number of event ids retained for peer de-duplication.
    /// </summary>
    public int KnownIdsLimit { get; set; } = 10000;

    /// <summary>
    /// Maximum live activity events broadcast per second per SlimFaas pod. 0 disables rate limiting.
    /// </summary>
    public int MaxLiveEventsPerSecond { get; set; }

    /// <summary>
    /// Live activity sampling ratio applied before broadcasting to SSE clients. 1.0 sends all events, 0 sends none.
    /// Stored events and peer synchronization are not sampled.
    /// </summary>
    public double LiveEventSamplingRatio { get; set; } = 1.0;

    /// <summary>
    /// Maximum number of live activity events grouped in a single SSE activity_batch frame.
    /// Batching lowers dashboard latency and browser overhead during bursts.
    /// </summary>
    public int LiveActivityBatchSize { get; set; } = 100;
}
