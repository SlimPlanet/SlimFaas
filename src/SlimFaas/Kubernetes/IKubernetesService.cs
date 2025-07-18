using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IKubernetesService
{
    Task<ReplicaRequest?> ScaleAsync(ReplicaRequest request);
    Task<DeploymentsInformations> ListFunctionsAsync(string kubeNamespace, DeploymentsInformations previousDeployments);

    Task CreateJobAsync(string kubeNamespace, string name, CreateJob createJob, string elementId,  string jobFullName, long inQueueTimestamp);
    Task<IList<Job>> ListJobsAsync(string ns);
    Task DeleteJobAsync(string kubeNamespace, string jobName);
}
