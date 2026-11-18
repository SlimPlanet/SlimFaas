using k8s.Models;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class KubernetesPodOwnershipTests
{
    [Fact]
    public void Deployments_with_a_shared_prefix_route_only_their_own_replica_sets()
    {
        V1ReplicaSetList sets = new() { Items = [ReplicaSet("api-a", "ra", "api", "a"), ReplicaSet("api-long-b", "rb", "api-long", "b")] };
        V1PodList pods = new() { Items = [Pod("short", "ReplicaSet", "api-a", "ra"), Pod("long", "ReplicaSet", "api-long-b", "rb")] };

        Assert.Equal(["short"], KubernetesService.GetControllerPodNames(pods, sets, "Deployment", "api", "a"));
        Assert.Equal(["long"], KubernetesService.GetControllerPodNames(pods, sets, "Deployment", "api-long", "b"));
    }

    [Fact]
    public void Rolling_deployments_include_both_owned_replica_sets()
    {
        V1ReplicaSetList sets = new() { Items = [ReplicaSet("api-old", "old", "api", "a"), ReplicaSet("api-new", "new", "api", "a")] };
        V1PodList pods = new() { Items = [Pod("old", "ReplicaSet", "api-old", "old"), Pod("new", "ReplicaSet", "api-new", "new")] };

        Assert.Equal(["new", "old"], KubernetesService.GetControllerPodNames(pods, sets, "Deployment", "api", "a").Order());
    }

    [Fact]
    public void Recreated_deployments_and_replica_sets_do_not_inherit_stale_owners()
    {
        V1ReplicaSetList sets = new() { Items = [ReplicaSet("api-hash", "current-rs", "api", "current-deployment")] };
        V1PodList pods = new() { Items = [Pod("stale", "ReplicaSet", "api-hash", "old-rs")] };

        Assert.Empty(KubernetesService.GetControllerPodNames(pods, sets, "Deployment", "api", "old-deployment"));
        Assert.Empty(KubernetesService.GetControllerPodNames(pods, sets, "Deployment", "api", "current-deployment"));
    }

    [Fact]
    public void Stateful_sets_use_exact_controller_identity_instead_of_a_name_prefix()
    {
        V1PodList pods = new() { Items = [Pod("short", "StatefulSet", "api", "a"), Pod("long", "StatefulSet", "api-long", "b")] };

        Assert.Equal(["short"], KubernetesService.GetControllerPodNames(pods, null, "StatefulSet", "api", "a"));
        Assert.Empty(KubernetesService.GetControllerPodNames(pods, null, "StatefulSet", "api", "old"));
    }

    [Fact]
    public void Missing_or_non_controller_ownership_is_not_used_for_routing()
    {
        V1ReplicaSet set = ReplicaSet("api-a", "r", "api", "a");
        set.Metadata.OwnerReferences[0].Controller = false;
        V1PodList pods = new() { Items = [Pod("pod", "ReplicaSet", "api-a", "r")] };

        Assert.Empty(KubernetesService.GetControllerPodNames(pods, new V1ReplicaSetList { Items = [set] }, "Deployment", "api", "a"));
        Assert.Empty(KubernetesService.GetControllerPodNames(pods, null, "Deployment", "api", null));
    }

    private static V1OwnerReference Owner(string kind, string name, string uid) =>
        new() { Kind = kind, Name = name, Uid = uid, Controller = true };

    private static V1Pod Pod(string name, string kind, string owner, string uid) =>
        new() { Metadata = new V1ObjectMeta { Name = name, OwnerReferences = [Owner(kind, owner, uid)] } };

    private static V1ReplicaSet ReplicaSet(string name, string uid, string deployment, string deploymentUid) =>
        new() { Metadata = new V1ObjectMeta { Name = name, Uid = uid, OwnerReferences = [Owner("Deployment", deployment, deploymentUid)] } };
}
