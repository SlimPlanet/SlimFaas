namespace SlimFaasMcpGateway.Options;

public sealed class ObservabilityOptions
{
    public OpenTelemetryOptions OpenTelemetry { get; set; } = new();
}

public sealed class OpenTelemetryOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Console | Otlp</summary>
    public string Exporter { get; set; } = "Console";

    /// <summary>OTLP endpoint URL (e.g., http://localhost:4317)</summary>
    public string? OtlpEndpoint { get; set; }
}
