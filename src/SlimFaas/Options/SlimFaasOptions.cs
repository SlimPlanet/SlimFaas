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
}
