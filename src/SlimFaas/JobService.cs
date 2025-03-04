using System.Collections;
using SlimFaas.Kubernetes;
using System.Threading;
using MemoryPack;

namespace SlimFaas;

public interface IJobService
{
    Task CreateJobAsync(string name, CreateJob createJob);
    Task<IList<Job>> SyncJobsAsync();
    IList<Job> Jobs { get; }
    Task<EnqueueJobResult> EnqueueJobAsync(string name, CreateJob createJob, bool isMessageComeFromNamespaceInternal);
}

public record EnqueueJobResult(string ErrorKey, int Code=400);

public class JobService(IKubernetesService kubernetesService, JobConfiguration jobConfiguration, IJobQueue jobQueue) : IJobService
{
    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

    public async Task CreateJobAsync(string name, CreateJob createJob)
    {
        await kubernetesService.CreateJobAsync(_namespace, name, createJob);
    }

    public async Task<EnqueueJobResult> EnqueueJobAsync(string name, CreateJob createJob, bool isMessageComeFromNamespaceInternal)
    {
        var configuration = jobConfiguration.Configuration.Configurations;
        if (!jobConfiguration.Configuration.Configurations.ContainsKey(name))
        {
            return new EnqueueJobResult("Configuration_not_found");
        }
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == FunctionVisibility.Private)
        {
            return new EnqueueJobResult("Visibility private", 400);
        }


        if (createJob.Image != string.Empty && !conf.ImagesWhitelist.Contains(createJob.Image))
        {
            return new EnqueueJobResult("Image_not_allowed");
        }
        var image = createJob.Image != string.Empty ? createJob.Image : conf.Image;

        var environments = new List<EnvVarInput>();
        foreach (var env in conf.Environments?.ToList() ?? new List<EnvVarInput>())
        {
            if((createJob.Environments ?? new List<EnvVarInput>()).All(e => e.Name != env.Name))
            {
                environments.Add(env);
            }
        }

        foreach (var env in createJob.Environments ?? new List<EnvVarInput>())
        {
            environments.Add(env);
        }

        CreateJob newCreateJob = new(image,
            createJob.Args,
            TtlSecondsAfterFinished: conf.TtlSecondsAfterFinished,
            Resources: JobResourceValidator.ValidateResources(conf.Resources,  createJob.Resources),
            Environments: environments,
            ConfigurationName: name
            );

        var createJobSerialized = MemoryPackSerializer.Serialize(newCreateJob);
        await jobQueue.EnqueueAsync(name, createJobSerialized);

        return new EnqueueJobResult(string.Empty, 204);
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
