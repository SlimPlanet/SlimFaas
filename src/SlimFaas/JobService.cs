using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IJobService
{
   Task CreateJobAsync(string name, CreateJob createJob);
}

public class JobService(IKubernetesService kubernetesService, IDatabaseService databaseService) : IJobService
{
    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

    public async Task CreateJobAsync(string name, CreateJob createJob)
    {
        await kubernetesService.CreateJobAsync(_namespace, name, createJob);
    }


}
