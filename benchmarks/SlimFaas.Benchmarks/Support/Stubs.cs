using System.Collections.Immutable;
using SlimData;
using SlimFaas;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Benchmarks.Support;

/// <summary>
/// Factory of synthetic data representative of a medium-sized cluster.
/// </summary>
public static class BenchData
{
    public static DeploymentsInformations BuildDeployments(int functionCount, int podsPerFunction)
    {
        var functions = new List<DeploymentInformation>(functionCount);
        for (var f = 0; f < functionCount; f++)
        {
            var name = $"function-{f}";
            var pods = new List<PodInformation>(podsPerFunction);
            for (var p = 0; p < podsPerFunction; p++)
            {
                pods.Add(new PodInformation(
                    Name: $"{name}-pod-{p}",
                    Started: true,
                    Ready: true,
                    Ip: $"10.42.{f}.{p + 1}",
                    DeploymentName: name,
                    Ports: [8080]));
            }

            functions.Add(new DeploymentInformation(
                Deployment: name,
                Namespace: "bench",
                Pods: pods,
                Configuration: new SlimFaasConfiguration(),
                Replicas: podsPerFunction,
                EndpointReady: true));
        }

        var slimFaasPods = new List<PodInformation>
        {
            new("slimfaas-0", true, true, "10.42.100.1", "slimfaas", [5000]),
            new("slimfaas-1", true, true, "10.42.100.2", "slimfaas", [5000]),
            new("slimfaas-2", true, true, "10.42.100.3", "slimfaas", [5000])
        };

        return new DeploymentsInformations(
            functions,
            new SlimFaasDeploymentInformation(slimFaasPods.Count, slimFaasPods),
            new List<PodInformation>(),
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
    }

    public static IList<Job> BuildJobs(int count)
    {
        var jobs = new List<Job>(count);
        for (var i = 0; i < count; i++)
        {
            jobs.Add(new Job(
                Name: $"job-{i}-slimfaas-job-{i:D4}",
                Status: JobStatus.Running,
                Ips: [$"10.43.0.{i + 1}"],
                DependsOn: [],
                ElementId: $"element-{i}",
                InQueueTimestamp: i,
                StartTimestamp: i));
        }

        return jobs;
    }

    public static ImmutableArray<QueueElement> BuildQueue(int depth, int payloadBytes)
    {
        var payload = new byte[payloadBytes];
        Random.Shared.NextBytes(payload);
        var builder = ImmutableArray.CreateBuilder<QueueElement>(depth);
        var nowTicks = DateTime.UtcNow.Ticks;
        for (var i = 0; i < depth; i++)
        {
            builder.Add(new QueueElement(
                value: payload,
                id: $"element-{i:D6}",
                insertTimeStamp: nowTicks,
                httpTimeoutSeconds: 30,
                timeoutRetriesSeconds: [2, 4, 8],
                retryQueueElements: ImmutableArray<QueueHttpTryElement>.Empty,
                httpStatusRetries: [500, 502, 503]));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Queue with a realistic mix of element states, as the Raft interpreter sees it
    /// on every dequeue: available (never tried), running (try in flight), timed out
    /// (try in flight past its HTTP timeout — will be marked 504 by the next pop) and
    /// finished (try completed with a non-retryable code — will be evicted by the next
    /// pop). Every element gets fresh <see cref="QueueHttpTryElement"/> instances so a
    /// benchmark can rebuild a pristine queue in its iteration setup.
    /// </summary>
    public static ImmutableArray<QueueElement> BuildMixedStateQueue(int depth, long nowTicks, int payloadBytes = 256)
    {
        var payload = new byte[payloadBytes];
        Random.Shared.NextBytes(payload);
        var builder = ImmutableArray.CreateBuilder<QueueElement>(depth);
        for (var i = 0; i < depth; i++)
        {
            var tries = (i % 4) switch
            {
                // available: never dispatched
                0 => ImmutableArray<QueueHttpTryElement>.Empty,
                // running: dispatched 1 s ago, 30 s timeout
                1 => [new QueueHttpTryElement(nowTicks - TimeSpan.TicksPerSecond, $"tx-{i:D6}")],
                // timed out: dispatched 60 s ago, still no answer
                2 => [new QueueHttpTryElement(nowTicks - 60 * TimeSpan.TicksPerSecond, $"tx-{i:D6}")],
                // finished: answered 200 half a second ago
                _ => [new QueueHttpTryElement(nowTicks - TimeSpan.TicksPerSecond, $"tx-{i:D6}", nowTicks - TimeSpan.TicksPerSecond / 2, 200)],
            };

            builder.Add(new QueueElement(
                value: payload,
                id: $"element-{i:D6}",
                insertTimeStamp: nowTicks - 2 * TimeSpan.TicksPerSecond,
                httpTimeoutSeconds: 30,
                timeoutRetriesSeconds: [2, 4, 8],
                retryQueueElements: tries,
                httpStatusRetries: [500, 502, 503, 504]));
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Queue where every element has one try in flight (the shape a callback batch
    /// meets: the function answered, SlimFaas reports the HTTP code back).
    /// </summary>
    public static ImmutableArray<QueueElement> BuildRunningQueue(int depth, long nowTicks, int payloadBytes = 256)
    {
        var payload = new byte[payloadBytes];
        Random.Shared.NextBytes(payload);
        var builder = ImmutableArray.CreateBuilder<QueueElement>(depth);
        for (var i = 0; i < depth; i++)
        {
            builder.Add(new QueueElement(
                value: payload,
                id: $"element-{i:D6}",
                insertTimeStamp: nowTicks - 2 * TimeSpan.TicksPerSecond,
                httpTimeoutSeconds: 30,
                timeoutRetriesSeconds: [2, 4, 8],
                retryQueueElements: [new QueueHttpTryElement(nowTicks - TimeSpan.TicksPerSecond, $"tx-{i:D6}")],
                httpStatusRetries: [500, 502, 503, 504]));
        }

        return builder.MoveToImmutable();
    }
}

/// <summary>
/// Test IKubernetesService: returns pre-built data, no network access.
/// </summary>
public sealed class StubKubernetesService(DeploymentsInformations deployments, IList<Job> jobs) : IKubernetesService
{
    public Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request) => Task.FromResult<ReplicaRequest?>(request);

    public Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace, DeploymentsInformations previousDeployments) =>
        Task.FromResult(deployments);

    public Task<SlimFaasJobConfiguration?> ListJobsConfigurationAsync(string kubeNamespace) =>
        Task.FromResult<SlimFaasJobConfiguration?>(null);

    public Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob, string elementId, string jobFullName, long inQueueTimestamp) =>
        Task.CompletedTask;

    public Task<IList<Job>> ListJobsAsync(string ns) => Task.FromResult(jobs);

    public Task DeleteJobAsync(string kubeNamespace, string jobName) => Task.CompletedTask;
}

public sealed class StubMetricsRegistry : IRequestedMetricsRegistry
{
    public void RegisterMetricName(string metricName)
    {
    }

    public void RegisterFromQuery(string promql)
    {
    }

    public IReadOnlyCollection<string> GetRequestedMetricNames() => [];

    public bool IsRequestedKey(string metricKey) => false;
}

public sealed class StubNamespaceProvider : INamespaceProvider
{
    public string CurrentNamespace => "bench";
}

public sealed class StubJobQueue : IJobQueue
{
    public Task<string> EnqueueAsync(string key, byte[] message) => Task.FromResult(string.Empty);

    public Task<IList<QueueData>?> DequeueAsync(string key, int count = 1) =>
        Task.FromResult<IList<QueueData>?>(null);

    public Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => Task.CompletedTask;

    public Task<IList<QueueData>> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue) =>
        Task.FromResult<IList<QueueData>>(new List<QueueData>());
}

public sealed class StubJobConfiguration : IJobConfiguration
{
    public SlimFaasJobConfiguration Configuration { get; set; } = new(new Dictionary<string, SlimfaasJob>());

    public Task<bool> SyncJobsConfigurationAsync() => Task.FromResult(true);
}

/// <summary>
/// IReplicasService that always returns the same instance: isolates the Proxy
/// benchmarks from the cost of ReplicasService.Deployments so that only the Proxy
/// code is measured.
/// </summary>
public sealed class StaticReplicasService(DeploymentsInformations deployments) : IReplicasService
{
    public DeploymentsInformations Deployments => deployments;

    public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => Task.FromResult(deployments);

    public Task CheckScaleAsync(string kubeNamespace) => Task.CompletedTask;
}
