using System.Collections;
using SlimFaas.Kubernetes;
using System.Threading;

namespace SlimFaas;

public interface IJobService
{
    Task CreateJobAsync(string name, CreateJob createJob);
    Task<IList<Job>> SyncJobsAsync();
    IList<Job> Jobs { get; }
}

public class JobService(IKubernetesService kubernetesService) : IJobService
{
    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

    public async Task CreateJobAsync(string name, CreateJob createJob)
    {
        await kubernetesService.CreateJobAsync(_namespace, name, createJob);
    }

    private IList<Job> _jobs = new List<Job>();

    public IList<Job> Jobs => new List<Job>(Volatile.Read(ref _jobs).ToArray());

    public async Task<IList<Job>> SyncJobsAsync()
    {
        var jobs = await kubernetesService.ListJobsAsync(_namespace);
        Interlocked.Exchange(ref _jobs, jobs);
        return jobs;
    }
}
