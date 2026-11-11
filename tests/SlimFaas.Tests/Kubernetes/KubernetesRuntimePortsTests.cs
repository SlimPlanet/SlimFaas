using k8s.Models;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

public sealed class KubernetesRuntimePortsTests
{
    [Theory]
    [InlineData(false, 3262)]
    [InlineData(true, 3262)]
    [InlineData(true, 13262)]
    public void Runtime_listeners_exclude_sidecars_regardless_of_container_order(bool sidecarFirst, int raftPort)
    {
        var runtime = Container("arbitrary-runtime-name", 5000, raftPort, 5003);
        var sidecar = Container("metrics-agent", 39777);
        V1Container[] containers = sidecarFirst ? [sidecar, runtime] : [runtime, sidecar];
        var pod = new PodInformation("slimfaas-0", true, true, "10.0.0.1", "slimfaas",
            containers.SelectMany(container => container.Ports).Select(port => port.ContainerPort).ToArray());

        IList<int> ports = KubernetesService.ResolveSlimFaasContainerPorts(
            containers, pod, $"http://{{pod_name}}.slimfaas.{{namespace}}.svc:{raftPort}", "test");

        Assert.Equal([5000, raftPort, 5003], ports);
        Assert.DoesNotContain(39777, ports);
    }

    [Fact]
    public void Single_container_keeps_its_declared_tcp_ports_without_a_declared_raft_listener()
    {
        V1Container container = Container("runtime", 5000, 5003);
        container.Ports.Add(new V1ContainerPort { ContainerPort = 5353, Protocol = "UDP" });
        var pod = new PodInformation("slimfaas-0", true, true, "10.0.0.1", "slimfaas");

        Assert.Equal([5000, 5003], KubernetesService.ResolveSlimFaasContainerPorts([container], pod));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ambiguous_multi_container_raft_ownership_fails_closed(bool duplicate)
    {
        var pod = new PodInformation("slimfaas-0", true, true, "10.0.0.1", "slimfaas");
        V1Container[] containers = [Container("one", 5000, duplicate ? 3262 : 5001),
                                   Container("two", 9000, duplicate ? 3262 : 9001)];

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            KubernetesService.ResolveSlimFaasContainerPorts(containers, pod));

        Assert.Contains("exactly one container", error.Message, StringComparison.Ordinal);
    }

    private static V1Container Container(string name, params int[] ports) =>
        new() { Name = name, Ports = ports.Select(port => new V1ContainerPort { ContainerPort = port }).ToList() };
}
