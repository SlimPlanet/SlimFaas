using k8s.Models;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
    internal static IList<int> ResolveSlimFaasContainerPorts(
        IList<V1Container>? containers, PodInformation pod, string? baseSlimDataUrl = null, string? kubeNamespace = null)
    {
        V1Container[] declared = containers?.Where(container => container.Ports?.Any(IsTcpPort) == true).ToArray() ?? [];
        if (declared.Length == 0)
            return [];
        if (declared.Length == 1)
            return declared[0].Ports.Where(IsTcpPort).Select(port => port.ContainerPort).Distinct().ToArray();

        string endpoint = SlimDataEndpoint.Get(pod, baseSlimDataUrl, kubeNamespace);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? raft))
            throw new InvalidOperationException($"Unable to resolve the SlimData endpoint for pod '{pod.Name}'.");
        V1Container[] owners = declared.Where(container => container.Ports.Any(port =>
            IsTcpPort(port) && port.ContainerPort == raft.Port)).ToArray();
        if (owners.Length != 1)
            throw new InvalidOperationException(
                $"Pod '{pod.Name}' must declare Raft TCP port {raft.Port} on exactly one container to distinguish SlimFaas listeners from sidecars.");
        return owners[0].Ports.Where(IsTcpPort).Select(port => port.ContainerPort).Distinct().ToArray();
    }

    private static bool IsTcpPort(V1ContainerPort port) => port.ContainerPort > 0 &&
        (string.IsNullOrEmpty(port.Protocol) || string.Equals(port.Protocol, "TCP", StringComparison.OrdinalIgnoreCase));
}
