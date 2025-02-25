using System.Collections;
using SlimFaas.Kubernetes;

namespace SlimFaas;

public interface IJobService
{
   Task CreateJobAsync(string name, CreateJob createJob);
   Task<IList<Job>> SyncJobsAsync();
   Task DeleteJobAsync(string jobName);


}

public class JobService(IKubernetesService kubernetesService) : IJobService
{
    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

    public async Task CreateJobAsync(string name, CreateJob createJob)
    {
        await kubernetesService.CreateJobAsync(_namespace, name, createJob);
    }

    private readonly object Lock = new();

    private IList<Job> _jobs = new List<Job>();

    public IList<Job> Jobs
    {
        get
        {
            lock (Lock)
            {
                return new List<Job>(_jobs.ToArray());
            }
        }
    }

    public async Task<IList<Job>> SyncJobsAsync()
    {
        return await kubernetesService.ListJobsAsync(_namespace);
    }

    public async Task DeleteJobAsync(string name)
    {
        await kubernetesService.DeleteJobAsync(_namespace, name);
    }

}
