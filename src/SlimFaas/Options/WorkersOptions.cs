namespace SlimFaas.Options;

/// <summary>
/// Configuration options for background workers
/// </summary>
public class WorkersOptions
{
    public const string SectionName = "Workers";

    /// <summary>
    /// Delay in milliseconds for the queues worker
    /// </summary>
    public int QueuesDelayMilliseconds { get; set; } = 10;

    /// <summary>
    /// Delay in milliseconds for the jobs worker
    /// </summary>
    public int JobsDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Delay in milliseconds for replicas synchronization
    /// </summary>
    public int ReplicasSynchronizationDelayMilliseconds { get; set; } = 3000;

    /// <summary>
    /// Delay in milliseconds for history synchronization
    /// </summary>
    public int HistorySynchronizationDelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// Delay in milliseconds for scaling replicas
    /// </summary>
    public int ScaleReplicasDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Delay in milliseconds for health checks
    /// </summary>
    public int HealthDelayMilliseconds { get; set; } = 1000;

    /// <summary>
    /// Delay in seconds before exiting when unhealthy
    /// </summary>
    public int HealthDelayToExitSeconds { get; set; } = 60;

    /// <summary>
    /// Delay in seconds before starting health checks
    /// </summary>
    public int HealthDelayToStartHealthCheckSeconds { get; set; } = 20;
}
