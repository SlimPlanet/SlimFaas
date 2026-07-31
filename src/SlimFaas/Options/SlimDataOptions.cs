namespace SlimFaas.Options;

public enum SlimDataBatchMode
{
    Global,
    PartitionedByKey
}

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
    /// Maximum number of WAL indices DotNext searches backwards when synchronizing a new member.
    /// </summary>
    public int WarmupRounds { get; set; } = 10_000;

    /// <summary>
    /// Local mutation queue topology.
    /// </summary>
    public SlimDataBatchMode BatchMode { get; set; } = SlimDataBatchMode.Global;

    /// <summary>
    /// Number of local key partitions when BatchMode is PartitionedByKey.
    /// </summary>
    public int BatchPartitionCount { get; set; } = 8;

    /// <summary>
    /// Removes local cooldown and coalescing while the mutation rate is low.
    /// </summary>
    public bool LowLoadFastPath { get; set; } = true;

    /// <summary>
    /// Maximum local mutation rate for the low-load fast path.
    /// </summary>
    public double LowLoadRequestsPerSecond { get; set; } = 10d;

    /// <summary>
    /// Directory path for ScheduleJob backup storage.
    /// When set, ScheduleJob data is backed up to this directory as JSON.
    /// On cold start with an empty database, data is restored from this backup.
    /// </summary>
    public string? BackupDirectory { get; set; }

    /// <summary>
    /// Interval in seconds between two periodic backup checks.
    /// The backup is only written when the data has actually changed (hash comparison).
    /// Default: 60 seconds. Ignored when BackupDirectory is not set.
    /// </summary>
    public int BackupIntervalSeconds { get; set; } = 60;
}
