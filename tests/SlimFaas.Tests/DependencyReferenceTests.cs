using System.Text.Json;
using SlimFaas.Kubernetes;
using SlimFaas.Local;

namespace SlimFaas.Tests;

public sealed class DependencyReferenceTests
{
    [Fact]
    public void LocalProcessDependency_IsIgnoredWithoutLocalProcessTopology()
    {
        DeploymentsInformations deployments = CreateDeployments(localProcesses: null);

        Assert.True(
            DependencyReference.IsLocalProcessReady(
                deployments,
                "processes:database-emulator"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalProcessDependency_UsesLocalRunningStatus(bool running)
    {
        DeploymentsInformations deployments = CreateDeployments(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["database-emulator"] = running
            });

        Assert.Equal(
            running,
            DependencyReference.IsLocalProcessReady(
                deployments,
                "processes:DATABASE-EMULATOR"));
    }

    [Fact]
    public void MissingLocalProcessDependency_IsNotReadyInLocalMode()
    {
        DeploymentsInformations deployments = CreateDeployments([]);

        Assert.False(
            DependencyReference.IsLocalProcessReady(
                deployments,
                "processes:missing"));
    }

    [Fact]
    public void ProcessControlJson_PreservesLocalProcessStatuses()
    {
        DeploymentsInformations deployments = CreateDeployments(
            new Dictionary<string, bool> { ["database-emulator"] = true });

        string json = JsonSerializer.Serialize(
            deployments,
            ProcessControlJsonContext.Default.DeploymentsInformations);
        DeploymentsInformations? deserialized = JsonSerializer.Deserialize(
            json,
            ProcessControlJsonContext.Default.DeploymentsInformations);

        Assert.True(deserialized?.LocalProcesses?["database-emulator"]);
    }

    private static DeploymentsInformations CreateDeployments(
        Dictionary<string, bool>? localProcesses)
        => new(
            Functions: [],
            SlimFaas: new SlimFaasDeploymentInformation(1, []),
            Pods: [],
            LocalProcesses: localProcesses);
}
