using k8s.Models;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public class PodObservationTests
{
    private static V1Container Container(string cpu, string memory, string? restart = null) => new()
    {
        Name = "container", RestartPolicy = restart, Resources = new()
        {
            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = cpu, ["memory"] = memory }
        }
    };

    [Fact]
    public void EffectiveRequestsUseConcurrentContainersInitPeaksPodOverridesAndOverhead()
    {
        var spec = new V1PodSpec
        {
            Containers = [Container("0.4", "64Mi"), Container("100m", "64Mi")],
            InitContainers = [Container("200m", "32Mi", "Always"), Container("0.7", "64Mi")],
            Overhead = new Dictionary<string, ResourceQuantity> { ["cpu"] = "10m", ["memory"] = "1Mi" }
        };
        Assert.Equal(new PodRequests(910, 161 * 1048576, 1, 0), PodObservation.Requests(spec));
        spec.Resources = new() { Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = "2" } };
        Assert.Equal(new PodRequests(2010, 161 * 1048576, 1, 0), PodObservation.Requests(spec));
        Assert.Equal(new PodRequests(0, 0, 1, 0), PodObservation.Requests(new V1PodSpec { Containers = [new()] }));
        Assert.Equal(PodRequests.Unavailable, PodObservation.Requests(null));
    }

    [Fact]
    public void LifecycleTracksStartingReadyCrashTerminationAndTerminalConditions()
    {
        var pod = new V1Pod { Metadata = new(), Status = new() { Phase = "Pending" } };
        var job = new V1Job { Metadata = new(), Status = new() };
        Assert.Equal("Starting", PodObservation.JobLifecycle(job, JobStatus.Running, [pod]));
        pod.Status.Conditions = [new() { Type = "Ready", Status = "True" }];
        Assert.Equal("Ready", PodObservation.Lifecycle(pod));
        Assert.Equal("Running", PodObservation.JobLifecycle(job, JobStatus.Running, [pod]));
        pod.Status.InitContainerStatuses = [new() { State = new() { Waiting = new() { Reason = "CrashLoopBackOff" } } }];
        Assert.Equal("Crash", PodObservation.Lifecycle(pod));
        Assert.Equal("Crash", PodObservation.JobLifecycle(job, JobStatus.Running, [pod]));
        pod.Metadata.DeletionTimestamp = DateTime.UtcNow;
        Assert.Equal("Terminating", PodObservation.Lifecycle(pod));
        Assert.Equal("Finishing", PodObservation.JobLifecycle(job, JobStatus.Running, [pod]));
        pod.Status.Phase = "Succeeded";
        Assert.Equal("Succeeded", PodObservation.Lifecycle(pod));
        Assert.Equal("Succeeded", PodObservation.JobLifecycle(job, JobStatus.Succeeded, [pod]));
        job.Status.Conditions = [new() { Type = "FailureTarget", Status = "True" }];
        Assert.Equal("Finishing", PodObservation.JobLifecycle(job, JobStatus.Running, []));
        Assert.Equal("Failed", PodObservation.JobLifecycle(job, JobStatus.Failed, []));
    }

    [Fact]
    public void StreamCountsPresentPodsOnceIncludingRolloutJobAndSlimFaas()
    {
        static PodInformation Pod(string name, string state, int cpu, int mib) =>
            new(name, true, state == "Ready", "", "workload") { Uid = name, Lifecycle = state,
                Requests = new(cpu, mib * 1048576, 1, 0) };
        var control = Pod("slimfaas-0", "Ready", 100, 64);
        var pods = new List<PodInformation> { Pod("old", "Terminating", 250, 128), Pod("new", "Starting", 250, 128),
            Pod("terminal", "Failed", 100, 64), control };
        var fn = FunctionEndpointsHelpers.MapToFunctionStatusDetailed(new("app", "test", pods, new(), 1));
        var job = new JobConfigurationStatus("report", "Private", "", [], 1, null, [], [],
            [new("run", "Running", "id", 0, 0) { Requests = new(500, 256 * 1048576, 1, 0) },
             new("previous", "Failed", "", 0, 0)], 0);
        var virtualFn = fn with { PodType = "WebSocket", Pods = [new("client", "Running", true, "client")] };
        var total = StatusStreamSnapshotCache.TotalRequests([fn, virtualFn], [control], [job]);
        Assert.Equal(new PodRequests(1100, 576 * 1048576, 4, 0), total);
        fn.Pods.RemoveAt(0);
        fn.Pods.Add(new("crash", "CrashLoopBackOff", false, "id") { Lifecycle = "Crash" });
        Assert.Equal(new PodRequests(850, 448 * 1048576, 3, 1),
            StatusStreamSnapshotCache.TotalRequests([fn], [control], [job]));
        Assert.Equal(PodRequests.Unavailable, StatusStreamSnapshotCache.TotalRequests([], [], [], jobsAvailable: false));
    }
}
