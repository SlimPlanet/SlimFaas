public class OpenTelemetryConfig
{
    public bool Enable { get; set; }
    public string? Endpoint { get; set; }
    public string? ServiceName { get; set; }
    public bool EnableConsoleExporter { get; set; }
    public string[]? ExcludedUrls { get; set; }
}
