using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IKubernetesService
{
    Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request);
    Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace, DeploymentsInformations previousDeployments);

    Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob);
}
