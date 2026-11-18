using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Integration;

public sealed class KubernetesTerminatingDestinationTests
{
    [Fact]
    public async Task A_terminating_pod_is_not_routable_even_when_its_containers_are_still_ready()
    {
        var cluster = new FakeKubernetesCluster(KubernetesSyncStack.Namespace);
        cluster.AddFunctionDeployment("terminating-function", 1);
        cluster.AddFunctionPod("terminating-function", "terminating-pod", "10.0.0.41");
        await using var stack = new KubernetesSyncStack(cluster, watchEnabled: false, pollingCadenceMilliseconds: 100);
        DeploymentsInformations before = await stack.ReplicasService.SyncDeploymentsAsync(KubernetesSyncStack.Namespace);
        PodInformation ready = Assert.Single(Assert.Single(before.Functions).Pods);
        Assert.True(ready.Ready);
        Assert.False(ready.IsTerminating);
        Assert.True(Guid.TryParse(ready.Uid, out _));

        // Kubernetes records deletion before the graceful shutdown stops the process.
        // Readiness conditions can still be true in this observable interval.
        cluster.SetPodTerminating("terminating-pod");
        DeploymentsInformations after = await stack.ReplicasService.SyncDeploymentsAsync(KubernetesSyncStack.Namespace);
        PodInformation terminating = Assert.Single(Assert.Single(after.Functions).Pods);
        Assert.False(terminating.Ready);
        Assert.True(terminating.IsTerminating);
        Assert.Equal(ready.Uid, terminating.Uid);
    }
}
