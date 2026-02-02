﻿using System.Configuration;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

[ExcludeFromCodeCoverage]
public record struct FunctionsMock
{
    public List<FunctionMock> Functions { get; set; }
    public List<SlimfaasMock> Slimfaas { get; set; }
}

[ExcludeFromCodeCoverage]
public record struct FunctionMock
{
    public int NumberParallelRequest { get; set; }

    public string Name { get; set; }
}

[ExcludeFromCodeCoverage]
public record struct SlimfaasMock
{
    public string Name { get; set; }
}

[ExcludeFromCodeCoverage]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(FunctionsMock))]
[JsonSerializable(typeof(FunctionMock))]
[JsonSerializable(typeof(List<FunctionMock>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class FunctionsMockSerializerContext : JsonSerializerContext
{

}

[ExcludeFromCodeCoverage]
public class MockKubernetesService : IKubernetesService
{

    private readonly DeploymentsInformations _deploymentInformations;
    public MockKubernetesService(IOptions<SlimFaasOptions> slimFaasOptions)
    {

        var functionsJson = slimFaasOptions.Value.MockKubernetesFunctions ?? "";
        var functions = JsonSerializer.Deserialize(functionsJson, FunctionsMockSerializerContext.Default.FunctionsMock);
        var slimfaasPods = new List<PodInformation>();
        foreach (var pod in functions.Slimfaas)
        {
            slimfaasPods.Add(new PodInformation(pod.Name, true, true, "localhost", "slimfaas"));
        }

        _deploymentInformations = new DeploymentsInformations(Functions: new List<DeploymentInformation>(),
            SlimFaas: new SlimFaasDeploymentInformation(Replicas: 1, slimfaasPods), new List<PodInformation>());

        foreach (var function in functions.Functions)
        {
            var deploymentInformation = new DeploymentInformation(Deployment: function.Name, Replicas: 1,
                ReplicasMin: 1, ReplicasAtStart: 1, TimeoutSecondBeforeSetReplicasMin: 1000000,
                Namespace: "default",
                ReplicasStartAsSoonAsOneFunctionRetrieveARequest: false,
                NumberParallelRequest: function.NumberParallelRequest,
                Pods: new List<PodInformation>() { new("", true, true, "", "") },
                Configuration : new SlimFaasConfiguration()
                );
            _deploymentInformations.Functions.Add(deploymentInformation);
        }
    }
    public Task<ReplicaRequest?> ScaleAsync(ReplicaRequest? request)
    {
        return Task.FromResult(request);
    }

    public Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace, DeploymentsInformations previousDeployments)
    {
        return Task.FromResult(_deploymentInformations);
    }

    public Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob, string elementId, string jobFullName,
        long inQueueTimestamp) => Task.CompletedTask;

    public Task<IList<Job>> ListJobsAsync(string ns) {
        return Task.FromResult<IList<Job>>(new List<Job>());
    }

    public Task DeleteJobAsync(string kubeNamespace, string jobName)
    {
        return Task.CompletedTask;
    }
}
