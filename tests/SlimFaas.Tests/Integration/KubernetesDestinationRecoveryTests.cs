using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Integration;

[Collection("KubernetesSyncIntegration")]
[Trait("Category", "Integration")]
public sealed class KubernetesDestinationRecoveryTests
{
    [Fact]
    public async Task Stateful_function_replacement_updates_destinations_without_a_controller_spec_change()
    {
        const string functionName = "stateful-function";
        var cluster = new FakeKubernetesCluster(KubernetesSyncStack.Namespace);
        cluster.AddFunctionStatefulSet(functionName, 1);
        cluster.AddFunctionPod(functionName, "stateful-function-0", "10.0.0.41", statefulSet: true);
        await using var stack = new KubernetesSyncStack(cluster, watchEnabled: true, pollingCadenceMilliseconds: 50);
        await stack.StartAsync();
        DeploymentInformation captured = Assert.Single(stack.ReplicasService.Deployments.Functions);
        PodInformation original = Assert.Single(captured.Pods);
        var proxy = new Proxy(stack.ReplicasService, functionName, captured);

        // A StatefulSet replacement keeps the pod name and controller metadata,
        // while Kubernetes assigns the new pod a different UID/IP/resourceVersion.
        cluster.DeletePod("stateful-function-0");
        cluster.AddFunctionPod(functionName, "stateful-function-0", "10.0.0.42", statefulSet: true);
        DeploymentsInformations refreshed = await stack.ReplicasService.SyncDeploymentsAsync(KubernetesSyncStack.Namespace);
        PodInformation replacement = Assert.Single(Assert.Single(refreshed.Functions).Pods);
        Assert.NotEqual(original.Uid, replacement.Uid);
        Assert.NotEqual(original.ResourceVersion, replacement.ResourceVersion);
        Assert.Equal("10.0.0.42", proxy.GetNextIP());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stateful_function_drops_an_unavailable_pod_when_another_pod_stays_ready(bool terminating)
    {
        const string functionName = "stateful-ready-pool";
        var cluster = new FakeKubernetesCluster(KubernetesSyncStack.Namespace);
        cluster.AddFunctionStatefulSet(functionName, 2);
        cluster.AddFunctionPod(functionName, "stateful-ready-pool-0", "10.0.0.41", statefulSet: true);
        cluster.AddFunctionPod(functionName, "stateful-ready-pool-1", "10.0.0.42", statefulSet: true);
        await using var stack = new KubernetesSyncStack(cluster, watchEnabled: false, pollingCadenceMilliseconds: 50);
        DeploymentsInformations before = await stack.ReplicasService.SyncDeploymentsAsync(KubernetesSyncStack.Namespace);
        var proxy = new Proxy(stack.ReplicasService, functionName, Assert.Single(before.Functions));

        if (terminating)
            cluster.SetPodTerminating("stateful-ready-pool-0");
        else
            cluster.SetPodReady("stateful-ready-pool-0", false);
        DeploymentsInformations after = await stack.ReplicasService.SyncDeploymentsAsync(KubernetesSyncStack.Namespace);
        DeploymentInformation function = Assert.Single(after.Functions);
        Assert.True(function.EndpointReady);
        Assert.False(Assert.Single(function.Pods, pod => pod.Name == "stateful-ready-pool-0").Ready);
        Assert.Equal("10.0.0.42", proxy.GetNextIP());
        Assert.Equal("10.0.0.42", proxy.GetNextIP());
    }

    [Fact]
    public async Task Failed_watch_and_list_retain_the_snapshot_until_list_access_returns()
    {
        const string functionName = "unavailable-api";
        var cluster = CreateCluster(functionName);
        await using var stack = new KubernetesSyncStack(cluster, watchEnabled: true, pollingCadenceMilliseconds: 50);
        await stack.StartAsync();
        await KubernetesSyncStack.WaitUntilAsync(() => stack.Signals.Functions.IsHealthy, "initial watches connected");

        cluster.SetApiAvailability(listAvailable: false, watchAvailable: false);
        int listCount = cluster.GetListCount("deployments");
        await KubernetesSyncStack.WaitUntilAsync(
            () => !stack.Signals.Functions.IsHealthy && cluster.GetListCount("deployments") >= listCount + 3,
            "watch disconnect and failed fallback LISTs");
        DeploymentsInformations retained = stack.ReplicasService.Deployments;
        PodInformation previousPod = Assert.Single(Assert.Single(retained.Functions).Pods);
        var capturedProxy = new Proxy(stack.ReplicasService, functionName, Assert.Single(retained.Functions));

        cluster.DeletePod("original-pod");
        cluster.AddFunctionPod(functionName, "replacement-pod", "10.0.0.42");
        listCount = cluster.GetListCount("deployments");
        await KubernetesSyncStack.WaitUntilAsync(
            () => cluster.GetListCount("deployments") >= listCount + 3,
            "LIST failures after the replacement");
        Assert.Same(retained, stack.ReplicasService.Deployments);
        Assert.Equal("10.0.0.41", capturedProxy.GetNextIP());

        // Recovery starts at restored API access, not at the deletion that happened
        // while both sources of Kubernetes state were unavailable. WATCH stays down:
        // the polling fallback alone must replace the cache within the routing budget.
        cluster.SetApiAvailability(listAvailable: true, watchAvailable: false);
        await KubernetesSyncStack.WaitUntilAsync(
            () => capturedProxy.GetNextIP() == "10.0.0.42",
            "fallback LIST removes the deleted destination", timeoutSeconds: 15);
        Assert.NotSame(retained, stack.ReplicasService.Deployments);
        PodInformation replacement = Assert.Single(Assert.Single(stack.ReplicasService.Deployments.Functions).Pods);
        Assert.NotEqual(previousPod.Uid, replacement.Uid);

        cluster.SetApiAvailability(listAvailable: true, watchAvailable: true);
        await KubernetesSyncStack.WaitUntilAsync(() => stack.Signals.Functions.IsHealthy, "watch recovery");
    }

    [Fact]
    public async Task Expired_watch_reconciles_a_pod_deleted_during_the_reconnect_gap()
    {
        const string functionName = "expired-watch";
        var cluster = CreateCluster(functionName);
        await using var stack = new KubernetesSyncStack(cluster, watchEnabled: true, pollingCadenceMilliseconds: 50);
        await stack.StartAsync();
        await KubernetesSyncStack.WaitUntilAsync(() => stack.Signals.Functions.IsHealthy, "initial watches connected");
        DeploymentInformation captured = Assert.Single(stack.ReplicasService.Deployments.Functions);
        var proxy = new Proxy(stack.ReplicasService, functionName, captured);
        int connections = cluster.TotalWatchConnections;

        cluster.ExpirePodWatches();
        // The fake drops events on the expired streams, reproducing a deletion in
        // history that the reconnecting client cannot replay from its old version.
        cluster.DeletePod("original-pod");
        cluster.AddFunctionPod(functionName, "replacement-pod", "10.0.0.42");
        try
        {
            await KubernetesSyncStack.WaitUntilAsync(
                () => cluster.TotalWatchConnections >= connections + 2 &&
                      stack.Signals.Functions.IsHealthy && proxy.GetNextIP() == "10.0.0.42",
                "410 recovery and current destinations", timeoutSeconds: 15);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException(
                $"410 recovery: watch connections {connections}->{cluster.TotalWatchConnections}, " +
                $"functions healthy={stack.Signals.Functions.IsHealthy}, destination={proxy.GetNextIP()}.", exception);
        }
        Assert.Equal("replacement-pod", Assert.Single(Assert.Single(stack.ReplicasService.Deployments.Functions).Pods).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scale_to_zero_and_wakeup_do_not_reuse_a_deleted_or_not_ready_pod(bool watchEnabled)
    {
        const string functionName = "sleeping-function";
        var cluster = CreateCluster(functionName);
        await using var stack = new KubernetesSyncStack(cluster, watchEnabled, pollingCadenceMilliseconds: 50);
        await stack.StartAsync();
        DeploymentInformation captured = Assert.Single(stack.ReplicasService.Deployments.Functions);
        var proxy = new Proxy(stack.ReplicasService, functionName, captured);

        cluster.SetDeploymentReplicas(functionName, 0);
        cluster.DeletePod("original-pod");
        await KubernetesSyncStack.WaitUntilAsync(
            () => Assert.Single(stack.ReplicasService.Deployments.Functions) is { Replicas: 0, Pods.Count: 0 },
            "scale to zero", timeoutSeconds: 15);
        Assert.Equal(string.Empty, proxy.AcquireNextIPForSync());

        cluster.SetDeploymentReplicas(functionName, 1);
        cluster.AddFunctionPod(functionName, "replacement-pod", "10.0.0.42", ready: false);
        await KubernetesSyncStack.WaitUntilAsync(
            () => Assert.Single(stack.ReplicasService.Deployments.Functions) is { Replicas: 1, Pods.Count: 1 },
            "waking pod visible", timeoutSeconds: 15);
        Assert.Equal(string.Empty, proxy.AcquireNextIPForSync());

        cluster.SetPodReady("replacement-pod", true);
        await KubernetesSyncStack.WaitUntilAsync(
            () => proxy.GetNextIP() == "10.0.0.42", "waking pod ready", timeoutSeconds: 15);
        string target = proxy.AcquireNextIPForSync();
        try
        {
            Assert.Equal("10.0.0.42", target);
        }
        finally
        {
            proxy.ReleaseSyncIP(target);
        }
    }

    private static FakeKubernetesCluster CreateCluster(string functionName)
    {
        var cluster = new FakeKubernetesCluster(KubernetesSyncStack.Namespace);
        cluster.AddFunctionDeployment(functionName, 1);
        cluster.AddFunctionPod(functionName, "original-pod", "10.0.0.41");
        return cluster;
    }
}
