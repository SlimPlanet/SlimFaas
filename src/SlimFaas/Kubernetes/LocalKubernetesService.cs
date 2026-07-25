using Microsoft.Extensions.Options;
using SlimFaas.Options;

namespace SlimFaas.Kubernetes;

/// <summary>
/// Deterministic, process-local implementation of <see cref="IKubernetesService"/>.
/// It describes a real multi-process SlimFaas topology without requiring a
/// Kubernetes or Docker daemon. SlimData still uses its normal HTTP Raft
/// transport, so no database or queue behaviour is mocked.
/// </summary>
public sealed class LocalKubernetesService : IKubernetesService
{
    private readonly LocalOrchestratorOptions _options;
    private readonly ILogger<LocalKubernetesService> _logger;
    private int _functionReplicas = 1;

    public LocalKubernetesService(
        IOptions<SlimFaasOptions> slimFaasOptions,
        ILogger<LocalKubernetesService> logger)
    {
        _options = slimFaasOptions.Value.Local;
        _logger = logger;
        Validate(_options);
    }

    public Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request)
    {
        if (!string.Equals(request.Deployment, _options.FunctionName, StringComparison.Ordinal))
            return Task.FromResult<ReplicaRequest?>(request);

        int replicas = Math.Clamp(request.Replicas, 0, 1);
        Volatile.Write(ref _functionReplicas, replicas);
        _logger.LogInformation(
            "Local function {FunctionName} scaled to {Replicas}",
            request.Deployment,
            replicas);
        return Task.FromResult<ReplicaRequest?>(request with { Replicas = replicas });
    }

    public Task<DeploymentsInformations> ListFunctionsAsync(
        string kubeNamespace,
        DeploymentsInformations previousDeployments)
    {
        var slimFaasPods = CreateSlimFaasPods();
        int replicas = Volatile.Read(ref _functionReplicas);
        IList<PodInformation> functionPods = replicas == 0
            ? []
            :
            [
                new PodInformation(
                    Name: $"{_options.FunctionName}-0",
                    Started: true,
                    Ready: true,
                    Ip: _options.FunctionHost,
                    DeploymentName: _options.FunctionName,
                    Ports: [_options.FunctionPort],
                    ResourceVersion: "local-function-1",
                    ServiceName: _options.FunctionName)
            ];

        var function = new DeploymentInformation(
            Deployment: _options.FunctionName,
            Namespace: kubeNamespace,
            Pods: functionPods,
            Configuration: new SlimFaasConfiguration(),
            Replicas: replicas,
            ReplicasAtStart: 1,
            ReplicasMin: 1,
            TimeoutSecondBeforeSetReplicasMin: int.MaxValue,
            NumberParallelRequest: _options.NumberParallelRequest,
            ResourceVersion: "local-function-1",
            EndpointReady: replicas > 0,
            NumberParallelRequestPerPod: _options.NumberParallelRequest);

        var snapshot = new DeploymentsInformations(
            Functions: [function],
            SlimFaas: new SlimFaasDeploymentInformation(_options.NodeCount, slimFaasPods),
            Pods: functionPods);

        return Task.FromResult(snapshot);
    }

    public Task<SlimFaasJobConfiguration?> ListJobsConfigurationAsync(string kubeNamespace)
        => Task.FromResult<SlimFaasJobConfiguration?>(null);

    public Task CreateJobAsync(
        string kubeNamespace,
        string name,
        CreateJob createJob,
        string elementId,
        string jobFullName,
        long inQueueTimestamp)
        => Task.CompletedTask;

    public Task<IList<Job>> ListJobsAsync(string ns)
        => Task.FromResult<IList<Job>>([]);

    public Task DeleteJobAsync(string kubeNamespace, string jobName)
        => Task.CompletedTask;

    private IList<PodInformation> CreateSlimFaasPods()
    {
        var result = new List<PodInformation>(_options.NodeCount);
        for (var index = 0; index < _options.NodeCount; index++)
        {
            result.Add(new PodInformation(
                Name: $"{_options.NodeNamePrefix}{index}",
                Started: true,
                Ready: true,
                Ip: "127.0.0.1",
                DeploymentName: "slimfaas",
                Ports:
                [
                    checked(_options.SlimDataPortBase + index),
                    checked(_options.HttpPortBase + index)
                ],
                ResourceVersion: $"local-node-{index + 1}",
                ServiceName: "slimfaas"));
        }

        return result;
    }

    private static void Validate(LocalOrchestratorOptions options)
    {
        if (options.NodeCount is < 1 or > 32)
            throw new InvalidOperationException("SlimFaas:Local:NodeCount must be between 1 and 32.");
        if (string.IsNullOrWhiteSpace(options.NodeNamePrefix))
            throw new InvalidOperationException("SlimFaas:Local:NodeNamePrefix must not be empty.");
        if (options.SlimDataPortBase is < 1 or > 65535 ||
            options.HttpPortBase is < 1 or > 65535 ||
            options.FunctionPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("SlimFaas local ports must be between 1 and 65535.");
        }
        if (options.SlimDataPortBase > 65536 - options.NodeCount)
            throw new InvalidOperationException("SlimFaas local SlimData port range exceeds 65535.");
        if (options.HttpPortBase > 65536 - options.NodeCount)
            throw new InvalidOperationException("SlimFaas local HTTP port range exceeds 65535.");
        if (string.IsNullOrWhiteSpace(options.FunctionName))
            throw new InvalidOperationException("SlimFaas:Local:FunctionName must not be empty.");
        if (string.IsNullOrWhiteSpace(options.FunctionHost))
            throw new InvalidOperationException("SlimFaas:Local:FunctionHost must not be empty.");
        if (options.NumberParallelRequest < 1)
            throw new InvalidOperationException("SlimFaas:Local:NumberParallelRequest must be positive.");
    }
}
