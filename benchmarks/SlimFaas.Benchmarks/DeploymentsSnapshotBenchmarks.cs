using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SlimFaas.Benchmarks.Support;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 1 — cost of reading the shared state snapshots.
/// `ReplicasService.Deployments` is read several times per proxied HTTP request and
/// up to ~100×/s by the queue workers; `JobService.Jobs` is read 2-3× per request.
/// </summary>
[MemoryDiagnoser]
public class DeploymentsSnapshotBenchmarks
{
    private ReplicasService _replicasService = null!;
    private JobService _jobService = null!;

    [GlobalSetup]
    public void Setup()
    {
        var deployments = BenchData.BuildDeployments(functionCount: 20, podsPerFunction: 5);
        var jobs = BenchData.BuildJobs(10);
        var kubernetes = new StubKubernetesService(deployments, jobs);

        _replicasService = new ReplicasService(
            kubernetes,
            new HistoryHttpMemoryService(),
            autoScaler: null!, // never used by Deployments / SyncDeploymentsAsync
            NullLogger<ReplicasService>.Instance,
            new StubMetricsRegistry(),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()));
        _replicasService.SyncDeploymentsAsync("bench").GetAwaiter().GetResult();

        _jobService = new JobService(
            kubernetes,
            new StubJobConfiguration(),
            new StubJobQueue(),
            new StubNamespaceProvider(),
            NullLogger<JobService>.Instance);
        _jobService.SyncJobsAsync().GetAwaiter().GetResult();
    }

    [Benchmark]
    public DeploymentsInformations ReadDeployments() => _replicasService.Deployments;

    [Benchmark]
    public DeploymentInformation? SearchFunction() =>
        _replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == "function-10");

    [Benchmark]
    public IList<Job> ReadJobs() => _jobService.Jobs;
}
