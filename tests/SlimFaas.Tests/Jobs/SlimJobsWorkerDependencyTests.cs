using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Jobs;

public sealed class SlimJobsWorkerDependencyTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void LocalProcessDependency_IsOnlyAppliedToLocalTopology(
        bool? running,
        bool expected)
    {
        Dictionary<string, bool>? localProcesses = running.HasValue
            ? new Dictionary<string, bool> { ["database-emulator"] = running.Value }
            : null;
        var deployments = new DeploymentsInformations(
            Functions: [],
            SlimFaas: new SlimFaasDeploymentInformation(1, []),
            Pods: [],
            LocalProcesses: localProcesses);

        Assert.Equal(
            expected,
            SlimJobsWorker.IsDependencyReady(
                deployments,
                "processes:database-emulator"));
    }
}
