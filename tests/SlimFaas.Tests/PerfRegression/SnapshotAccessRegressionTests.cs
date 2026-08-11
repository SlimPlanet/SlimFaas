using Microsoft.Extensions.Logging;
using Moq;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.PerfRegression;

// Non-regression tests protecting the observable behavior of the
// ReplicasService.Deployments and JobService.Jobs properties ("defensive copies"
// theme): the returned content must reflect exactly the last synchronization,
// whatever the internal strategy (copy or shared atomic snapshot).
public class SnapshotAccessRegressionTests
{
    private static DeploymentsInformations BuildDeployments(string suffix, int functionCount)
    {
        var functions = new List<DeploymentInformation>();
        for (var i = 0; i < functionCount; i++)
        {
            functions.Add(new DeploymentInformation(
                $"function-{suffix}-{i}",
                "unit-test",
                new List<PodInformation> { new($"pod-{i}", true, true, $"10.0.0.{i + 1}", $"function-{suffix}-{i}") },
                new SlimFaasConfiguration(),
                Replicas: 1));
        }

        return new DeploymentsInformations(
            functions,
            new SlimFaasDeploymentInformation(2, new List<PodInformation>
            {
                new("slimfaas-0", true, true, "10.0.100.1", "slimfaas")
            }),
            new List<PodInformation>(),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["local-process"] = true });
    }

    private static ReplicasService BuildReplicasService(Mock<IKubernetesService> kubernetesService) =>
        new(
            kubernetesService.Object,
            new HistoryHttpMemoryService(),
            autoScaler: null!,
            new Mock<ILogger<ReplicasService>>().Object,
            new Mock<IRequestedMetricsRegistry>().Object,
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()));

    [Fact]
    public async Task DeploymentsReflectsTheLastSynchronization()
    {
        var kubernetesService = new Mock<IKubernetesService>();
        var first = BuildDeployments("a", 3);
        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(first);
        var replicasService = BuildReplicasService(kubernetesService);

        await replicasService.SyncDeploymentsAsync("unit-test");

        DeploymentsInformations snapshot = replicasService.Deployments;
        Assert.Equal(3, snapshot.Functions.Count);
        Assert.Equal("function-a-0", snapshot.Functions[0].Deployment);
        Assert.Equal(2, snapshot.SlimFaas.Replicas);
        Assert.Single(snapshot.SlimFaas.Pods);
        Assert.NotNull(snapshot.LocalProcesses);
        Assert.True(snapshot.LocalProcesses!["local-process"]);

        // A new synchronization must be visible immediately.
        var second = BuildDeployments("b", 5);
        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(second);
        await replicasService.SyncDeploymentsAsync("unit-test");

        DeploymentsInformations refreshed = replicasService.Deployments;
        Assert.Equal(5, refreshed.Functions.Count);
        Assert.Equal("function-b-0", refreshed.Functions[0].Deployment);
    }

    [Fact]
    public async Task DeploymentsReadsAreStableBetweenSynchronizations()
    {
        var kubernetesService = new Mock<IKubernetesService>();
        kubernetesService
            .Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(BuildDeployments("a", 2));
        var replicasService = BuildReplicasService(kubernetesService);
        await replicasService.SyncDeploymentsAsync("unit-test");

        DeploymentsInformations read1 = replicasService.Deployments;
        DeploymentsInformations read2 = replicasService.Deployments;

        Assert.Equal(read1.Functions.Count, read2.Functions.Count);
        for (var i = 0; i < read1.Functions.Count; i++)
        {
            Assert.Equal(read1.Functions[i], read2.Functions[i]);
        }
    }

    [Fact]
    public async Task JobsReflectsTheLastSynchronization()
    {
        var kubernetesService = new Mock<IKubernetesService>();
        IList<Job> first = new List<Job>
        {
            new("job-a", JobStatus.Running, new List<string> { "10.1.0.1" }, new List<string>(), "element-1", 1, 1)
        };
        kubernetesService.Setup(k => k.ListJobsAsync(It.IsAny<string>())).ReturnsAsync(first);
        var namespaceProvider = new Mock<INamespaceProvider>();
        namespaceProvider.Setup(n => n.CurrentNamespace).Returns("unit-test");
        var jobService = new JobService(
            kubernetesService.Object,
            new Mock<IJobConfiguration>().Object,
            new Mock<IJobQueue>().Object,
            namespaceProvider.Object,
            new Mock<ILogger<JobService>>().Object);

        await jobService.SyncJobsAsync();
        Assert.Single(jobService.Jobs);
        Assert.Equal("job-a", jobService.Jobs[0].Name);

        IList<Job> second = new List<Job>
        {
            new("job-b", JobStatus.Pending, new List<string>(), new List<string>(), "element-2", 2, 2),
            new("job-c", JobStatus.Running, new List<string>(), new List<string>(), "element-3", 3, 3)
        };
        kubernetesService.Setup(k => k.ListJobsAsync(It.IsAny<string>())).ReturnsAsync(second);
        await jobService.SyncJobsAsync();

        Assert.Equal(2, jobService.Jobs.Count);
        Assert.Equal("job-b", jobService.Jobs[0].Name);
    }
}
