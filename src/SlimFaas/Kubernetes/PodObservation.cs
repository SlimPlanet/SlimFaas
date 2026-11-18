using k8s.Models;

namespace SlimFaas.Kubernetes;

/// <summary>Effective requests of observed, nonterminal pods, in millicores and bytes.</summary>
public sealed record PodRequests(decimal CpuMillis, decimal MemoryBytes, int KnownPods, int UnknownPods)
{
    internal static readonly PodRequests Empty = new(0, 0, 0, 0);
    internal static readonly PodRequests Unavailable = new(0, 0, 0, 1);

    internal PodRequests Add(PodRequests other) => new(CpuMillis + other.CpuMillis, MemoryBytes + other.MemoryBytes,
        KnownPods + other.KnownPods, UnknownPods + other.UnknownPods);
}

internal static class PodObservation
{
    internal static bool IsTerminal(string? lifecycle) => lifecycle is "Succeeded" or "Failed";

    internal static string Lifecycle(V1Pod pod)
    {
        if (pod.Status?.Phase is "Failed" or "Succeeded") return pod.Status.Phase;
        if (pod.Metadata?.DeletionTimestamp is not null) return "Terminating";
        if (HasFailure(pod.Status?.InitContainerStatuses) || HasFailure(pod.Status?.ContainerStatuses))
            return "Crash";
        if (pod.Status?.Conditions?.Any(c => c is { Type: "Ready", Status: "True" }) == true) return "Ready";
        return pod.Status?.Phase == "Unknown" ? "Unknown" : "Starting";
    }

    private static bool HasFailure(IList<V1ContainerStatus>? containers) =>
        containers?.Any(c => c.State?.Terminated is { ExitCode: not 0 } ||
            c.State?.Waiting?.Reason is "CrashLoopBackOff" or "ImagePullBackOff" or "ErrImagePull" or
            "CreateContainerConfigError" or "CreateContainerError" or "RunContainerError" or "InvalidImageName") == true;

    internal static PodRequests Requests(V1PodSpec? spec)
    {
        if (spec?.Containers is null) return PodRequests.Unavailable;
        return new(EffectiveRequest(spec, "cpu") * 1000, EffectiveRequest(spec, "memory"), 1, 0);
    }

    private static decimal EffectiveRequest(V1PodSpec spec, string resource)
    {
        decimal overhead = Quantity(spec.Overhead, resource);
        if (spec.Resources?.Requests?.TryGetValue(resource, out var podRequest) == true)
            return podRequest.ToDecimal() + overhead;

        decimal apps = spec.Containers.Sum(c => Quantity(c.Resources?.Requests, resource));
        decimal sidecars = 0;
        decimal initPeak = 0;
        foreach (var init in spec.InitContainers ?? [])
        {
            decimal request = Quantity(init.Resources?.Requests, resource);
            initPeak = Math.Max(initPeak, sidecars + request);
            if (init.RestartPolicy == "Always") sidecars += request;
        }
        return Math.Max(apps + sidecars, initPeak) + overhead;
    }

    private static decimal Quantity(IDictionary<string, ResourceQuantity>? quantities, string resource) =>
        quantities?.TryGetValue(resource, out var quantity) == true ? quantity.ToDecimal() : 0;

    internal static string JobLifecycle(V1Job job, JobStatus status, V1Pod[] pods)
    {
        if (status is JobStatus.Succeeded or JobStatus.Failed) return status.ToString();
        if (job.Metadata?.DeletionTimestamp is not null ||
            job.Status?.Conditions?.Any(c => c is { Status: "True", Type: "SuccessCriteriaMet" or "FailureTarget" }) == true)
            return "Finishing";
        var states = pods.Select(Lifecycle).ToHashSet(StringComparer.Ordinal);
        if (states.Contains("Crash")) return "Crash";
        if (states.Contains("Ready") || pods.Any(p => p.Status?.Phase == "Running" && p.Metadata?.DeletionTimestamp is null &&
            p.Status.ContainerStatuses?.Any(c => c.State?.Running is not null) == true)) return "Running";
        if (states.Contains("Starting")) return "Starting";
        if (states.Contains("Terminating") || states.SetEquals(["Succeeded"])) return "Finishing";
        return states.Contains("Unknown") ? "Unknown" : "Starting";
    }
}
