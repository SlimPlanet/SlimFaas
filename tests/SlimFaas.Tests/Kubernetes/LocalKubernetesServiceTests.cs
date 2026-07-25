using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Kubernetes;

public sealed class LocalKubernetesServiceTests
{
    [Fact]
    public async Task ListFunctionsAsync_DescribesThreeRealLocalNodes()
    {
        var service = CreateService();

        var result = await service.ListFunctionsAsync("memory", EmptyDeployments());

        Assert.Equal(3, result.SlimFaas.Replicas);
        Assert.Collection(
            result.SlimFaas.Pods,
            pod => AssertNode(pod, "slimfaas-0", 3262, 30021),
            pod => AssertNode(pod, "slimfaas-1", 3263, 30022),
            pod => AssertNode(pod, "slimfaas-2", 3264, 30023));

        var function = Assert.Single(result.Functions);
        Assert.Equal("memory-function", function.Deployment);
        Assert.True(function.EndpointReady);
        var functionPod = Assert.Single(function.Pods);
        Assert.Equal("127.0.0.1", functionPod.Ip);
        Assert.Equal([5050], functionPod.Ports);
    }

    [Fact]
    public async Task ScaleAsync_UpdatesTheFollowingSnapshot()
    {
        var service = CreateService();

        var scaled = await service.ScaleAsync(
            new ReplicaRequest("memory-function", "memory", 0, PodType.Deployment));
        var result = await service.ListFunctionsAsync("memory", EmptyDeployments());

        Assert.NotNull(scaled);
        Assert.Equal(0, scaled.Replicas);
        var function = Assert.Single(result.Functions);
        Assert.Equal(0, function.Replicas);
        Assert.False(function.EndpointReady);
        Assert.Empty(function.Pods);
    }

    [Fact]
    public void Constructor_RejectsAnInvalidPortRange()
    {
        var options = new SlimFaasOptions
        {
            Local = new LocalOrchestratorOptions
            {
                NodeCount = 3,
                SlimDataPortBase = 65535
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new LocalKubernetesService(
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<LocalKubernetesService>.Instance));

        Assert.Contains("port range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static LocalKubernetesService CreateService()
        => new(
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()),
            NullLogger<LocalKubernetesService>.Instance);

    private static DeploymentsInformations EmptyDeployments()
        => new(
            [],
            new SlimFaasDeploymentInformation(0, []),
            []);

    private static void AssertNode(
        PodInformation pod,
        string expectedName,
        int expectedSlimDataPort,
        int expectedHttpPort)
    {
        Assert.Equal(expectedName, pod.Name);
        Assert.True(pod.Started);
        Assert.True(pod.Ready);
        Assert.Equal("127.0.0.1", pod.Ip);
        Assert.Equal([expectedSlimDataPort, expectedHttpPort], pod.Ports);
    }
}
