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

    /// <summary>
    /// Directory path for ScheduleJob backup storage.
    /// When set, ScheduleJob data is backed up to this directory as JSON.
    /// On cold start with an empty database, data is restored from this backup.
    /// </summary>
    public string? BackupDirectory { get; set; }
}
