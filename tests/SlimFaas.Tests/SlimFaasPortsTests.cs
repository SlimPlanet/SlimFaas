using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests;

public sealed class SlimFaasPortsTests
{
    [Fact]
    public void ResolvePorts_UsesTheCurrentLocalNodeAndExcludesItsRaftPort()
    {
        var deployments = new DeploymentsInformations(
            [],
            new SlimFaasDeploymentInformation(
                3,
                [
                    Node("slimfaas-0", 3262, 30021),
                    Node("slimfaas-1", 3263, 30022),
                    Node("slimfaas-2", 3264, 30023)
                ]),
            []);

        var result = SlimFaasPorts.ResolvePorts(
            deployments,
            "http://{pod_ip}:{pod_port_0}",
            "memory",
            "slimfaas-1",
            NullLogger.Instance);

        Assert.Equal([30022], result);
    }

    private static PodInformation Node(string name, int slimDataPort, int httpPort)
        => new(
            name,
            true,
            true,
            "127.0.0.1",
            "slimfaas",
            [slimDataPort, httpPort],
            ServiceName: "slimfaas");
}
