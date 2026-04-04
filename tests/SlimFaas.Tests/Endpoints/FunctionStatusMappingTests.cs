using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Endpoints;

public class FunctionStatusMappingTests
{
    [Fact(DisplayName = "MapToFunctionStatusDetailed maps all fields correctly")]
    public void MapToFunctionStatusDetailed_MapsAllFields()
    {
        var deployment = new DeploymentInformation(
            Deployment: "my-func",
            Namespace: "default",
            Pods: new List<PodInformation>
            {
                new("pod-1", true, true, "10.0.0.1", "my-func", new List<int> { 8080 }),
                new("pod-2", true, false, "10.0.0.2", "my-func", new List<int> { 8080 })
                {
                    AppFailureReason = "CrashLoopBackOff"
                },
            },
            Configuration: new SlimFaasConfiguration(),
            Replicas: 2,
            ReplicasAtStart: 2,
            ReplicasMin: 1,
            TimeoutSecondBeforeSetReplicasMin: 600,
            NumberParallelRequest: 20,
            NumberParallelRequestPerPod: 5,
            Visibility: FunctionVisibility.Private,
            DependsOn: new List<string> { "dep-a", "dep-b" },
            SubscribeEvents: new List<SubscribeEvent>
            {
                new("reload", FunctionVisibility.Public)
            },
            PathsStartWithVisibility: new List<PathVisibility>
            {
                new("/admin", FunctionVisibility.Private)
            },
            Resources: new ResourcesConfiguration("100m", "500m", "128Mi", "512Mi"),
            Schedule: new ScheduleConfig
            {
                TimeZoneID = "UTC",
                Default = new DefaultSchedule
                {
                    WakeUp = new List<string> { "08:00" },
                    ScaleDownTimeout = new List<ScaleDownTimeout>()
                }
            }
        );

        var result = FunctionEndpointsHelpers.MapToFunctionStatusDetailed(deployment);

        Assert.Equal("my-func", result.Name);
        Assert.Equal(1, result.NumberReady); // pod-1 is ready
        Assert.Equal(2, result.NumberRequested);
        Assert.Equal("Deployment", result.PodType);
        Assert.Equal("Private", result.Visibility);
        Assert.Equal(1, result.ReplicasMin);
        Assert.Equal(2, result.ReplicasAtStart);
        Assert.Equal(600, result.TimeoutSecondBeforeSetReplicasMin);
        Assert.Equal(20, result.NumberParallelRequest);
        Assert.Equal(5, result.NumberParallelRequestPerPod);

        // Resources
        Assert.NotNull(result.Resources);
        Assert.Equal("100m", result.Resources.CpuRequest);
        Assert.Equal("500m", result.Resources.CpuLimit);
        Assert.Equal("128Mi", result.Resources.MemoryRequest);
        Assert.Equal("512Mi", result.Resources.MemoryLimit);

        // DependsOn
        Assert.NotNull(result.DependsOn);
        Assert.Equal(2, result.DependsOn.Count);
        Assert.Contains("dep-a", result.DependsOn);
        Assert.Contains("dep-b", result.DependsOn);

        // Events
        Assert.NotNull(result.SubscribeEvents);
        Assert.Single(result.SubscribeEvents);

        // Paths
        Assert.NotNull(result.PathsStartWithVisibility);
        Assert.Single(result.PathsStartWithVisibility);

        // Schedule
        Assert.NotNull(result.Schedule);
        Assert.Single(result.Schedule.Default!.WakeUp);

        // Pods
        Assert.Equal(2, result.Pods.Count);
        Assert.Equal("Running", result.Pods[0].Status);
        Assert.True(result.Pods[0].Ready);
        Assert.Equal("CrashLoopBackOff", result.Pods[1].Status);
        Assert.False(result.Pods[1].Ready);
    }

    [Fact(DisplayName = "MapToFunctionStatusDetailed handles null optional fields")]
    public void MapToFunctionStatusDetailed_HandlesNullOptionalFields()
    {
        var deployment = new DeploymentInformation(
            Deployment: "simple",
            Namespace: "ns",
            Pods: new List<PodInformation>(),
            Configuration: new SlimFaasConfiguration(),
            Replicas: 0
        );

        var result = FunctionEndpointsHelpers.MapToFunctionStatusDetailed(deployment);

        Assert.Equal("simple", result.Name);
        Assert.Equal(0, result.NumberReady);
        Assert.Equal(0, result.NumberRequested);
        Assert.Equal(10, result.NumberParallelRequest); // default
        Assert.Equal(10, result.NumberParallelRequestPerPod); // default
        Assert.Null(result.DependsOn);
        Assert.Null(result.Resources);
        Assert.Empty(result.Pods);
    }

    [Fact(DisplayName = "MapToFunctionStatusDetailed maps pending pods")]
    public void MapToFunctionStatusDetailed_MapsPendingPods()
    {
        var deployment = new DeploymentInformation(
            Deployment: "pending-func",
            Namespace: "ns",
            Pods: new List<PodInformation>
            {
                new("pod-pending", null, null, "", "pending-func"),
                new("pod-starting", true, false, "10.0.0.1", "pending-func"),
                new("pod-sched-fail", null, null, "", "pending-func")
                {
                    StartFailureReason = "Unschedulable"
                },
            },
            Configuration: new SlimFaasConfiguration(),
            Replicas: 3
        );

        var result = FunctionEndpointsHelpers.MapToFunctionStatusDetailed(deployment);

        Assert.Equal(3, result.Pods.Count);
        Assert.Equal("Pending", result.Pods[0].Status);
        Assert.Equal("Starting", result.Pods[1].Status);
        Assert.Equal("Unschedulable", result.Pods[2].Status);
        Assert.Equal(0, result.NumberReady);
    }

    [Fact(DisplayName = "MapToFunctionStatusDetailed maps WebSocket PodType correctly")]
    public void MapToFunctionStatusDetailed_MapsWebSocketPodType()
    {
        var deployment = new DeploymentInformation(
            Deployment: "ws-func",
            Namespace: "websocket-virtual",
            Pods: new List<PodInformation>
            {
                new("ws-abc12345", true, true, "abc12345", "ws-func"),
                new("ws-def67890", true, true, "def67890", "ws-func"),
            },
            Configuration: new SlimFaasConfiguration(),
            Replicas: 2,
            ReplicasAtStart: 2,
            ReplicasMin: 0,
            PodType: PodType.WebSocket,
            Visibility: FunctionVisibility.Public,
            DependsOn: new List<string> { "kafka" },
            SubscribeEvents: new List<SubscribeEvent>
            {
                new("chat-msg", FunctionVisibility.Public)
            }
        );

        var result = FunctionEndpointsHelpers.MapToFunctionStatusDetailed(deployment);

        Assert.Equal("ws-func", result.Name);
        Assert.Equal("WebSocket", result.PodType);
        Assert.Equal(2, result.NumberReady);
        Assert.Equal(2, result.NumberRequested);
        Assert.Equal("Public", result.Visibility);
        Assert.NotNull(result.DependsOn);
        Assert.Contains("kafka", result.DependsOn);
        Assert.NotNull(result.SubscribeEvents);
        Assert.Single(result.SubscribeEvents);
        Assert.Equal(2, result.Pods.Count);
        Assert.All(result.Pods, p => Assert.Equal("Running", p.Status));
    }

    [Fact(DisplayName = "MapToFunctionStatus maps WebSocket PodType correctly")]
    public void MapToFunctionStatus_MapsWebSocketPodType()
    {
        var deployment = new DeploymentInformation(
            Deployment: "ws-func",
            Namespace: "websocket-virtual",
            Pods: new List<PodInformation>
            {
                new("ws-abc12345", true, true, "abc12345", "ws-func"),
            },
            Configuration: new SlimFaasConfiguration(),
            Replicas: 1,
            PodType: PodType.WebSocket
        );

        var result = FunctionEndpointsHelpers.MapToFunctionStatus(deployment);

        Assert.Equal("ws-func", result.Name);
        Assert.Equal("WebSocket", result.PodType);
        Assert.Equal(1, result.NumberReady);
        Assert.Equal(1, result.NumberRequested);
    }
}

