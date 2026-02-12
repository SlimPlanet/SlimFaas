namespace SlimFaas.Options;

/// <summary>
/// Configuration options for SlimData
/// </summary>
public class SlimDataOptions
{
    public const string SectionName = "SlimData";

    /// <summary>
    /// Directory path for SlimData storage
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Configuration dictionary for SlimData (JSON format)
    /// </summary>
    public string? Configuration { get; set; }

    /// <summary>
    /// Allow cold start for SlimData
    /// </summary>
    public bool AllowColdStart { get; set; }
}
