namespace SlimFaas.Kubernetes;

public enum JobStatus
{
    Pending = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    ImagePullBackOff = 4,
}

public record Job(
    string Name,
    JobStatus Status,
    IList<string> Ips,
    IList<string> DependsOn,
    string ElementId,
    long InQueueTimestamp,
    long StartTimestamp);
