namespace SlimFaasMcp.Configuration;

public class OpenTelemetryConfig
{
    public string ServiceName { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public bool EnableConsoleExporter { get; set; } = false;
}

