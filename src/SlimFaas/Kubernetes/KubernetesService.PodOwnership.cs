using k8s.Models;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
    internal static IReadOnlySet<string> GetControllerPodNames(
        V1PodList pods, V1ReplicaSetList? replicaSets, string kind, string? name, string? uid)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uid))
            return result;
        var ownedReplicaSets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (kind == "Deployment" && replicaSets?.Items is { } sets)
        {
            foreach (V1ReplicaSet set in sets)
            {
                V1OwnerReference? owner = Controller(set.Metadata?.OwnerReferences);
                if (owner?.Kind == kind && owner.Name == name && owner.Uid == uid &&
                    !string.IsNullOrEmpty(set.Metadata?.Uid) && !string.IsNullOrEmpty(set.Metadata.Name))
                    ownedReplicaSets[set.Metadata.Uid] = set.Metadata.Name;
            }
        }
        foreach (V1Pod pod in pods.Items ?? [])
        {
            V1OwnerReference? owner = Controller(pod.Metadata?.OwnerReferences);
            if (owner is null || string.IsNullOrEmpty(pod.Metadata?.Name))
                continue;
            bool owned = kind == "Deployment"
                ? owner.Kind == "ReplicaSet" && !string.IsNullOrEmpty(owner.Uid) &&
                  ownedReplicaSets.TryGetValue(owner.Uid, out string? replicaSetName) && replicaSetName == owner.Name
                : owner.Kind == kind && owner.Name == name && owner.Uid == uid;
            if (owned)
                result.Add(pod.Metadata.Name);
        }
        return result;
    }

    private static V1OwnerReference? Controller(IList<V1OwnerReference>? references)
    {
        V1OwnerReference[] controllers = references?.Where(reference => reference.Controller == true).ToArray() ?? [];
        return controllers.Length == 1 ? controllers[0] : null;
    }
}
