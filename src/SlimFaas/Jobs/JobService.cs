using System.Text.RegularExpressions;
using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;

public interface IJobService
{
    Task CreateJobAsync(string name, CreateJob createJob);
    Task DeleteJobAsync(string name);
    Task<IList<Job>> SyncJobsAsync();
    IList<Job> Jobs { get; }
    Task<EnqueueJobResult> EnqueueJobAsync(string name, CreateJob createJob, bool isMessageComeFromNamespaceInternal);
}

public record EnqueueJobResult(string ErrorKey, int Code=400);

public class JobService(IKubernetesService kubernetesService, IJobConfiguration jobConfiguration, IJobQueue jobQueue) : IJobService
{
    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

    public async Task CreateJobAsync(string name, CreateJob createJob)
    {
        await kubernetesService.CreateJobAsync(_namespace, name, createJob);
    }

    public async Task DeleteJobAsync(string name)
    {
        await kubernetesService.DeleteJobAsync(_namespace, name);
    }

    public static string ConvertPatternToRegex(string pattern)
    {
        return "^" + Regex.Escape(pattern)
                       .Replace("\\*", ".*")  // '*' devient '.*'
                       .Replace(":", "\\:")    // Échapper les deux-points
                   + "$";
    }

    private static bool IsPatternMatch(string pattern, string target)
    {
        string regexPattern = ConvertPatternToRegex(pattern);
        return Regex.IsMatch(target, regexPattern);
    }

    private static bool IsImageAllowed(IList<string> imagesWhiteList, string image)
    {
        if (imagesWhiteList.Any(imageWhiteList => IsPatternMatch(imageWhiteList, image)))
        {
            return true;
        }

        return false;
    }

    public async Task<EnqueueJobResult> EnqueueJobAsync(string name, CreateJob createJob, bool isMessageComeFromNamespaceInternal)
    {
        var configuration = jobConfiguration.Configuration.Configurations;
        name = configuration.ContainsKey(name) ? name : "Default";
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return new EnqueueJobResult("Visibility private", 400);
        }

        if (createJob.Image != string.Empty && !IsImageAllowed(conf.ImagesWhitelist, createJob.Image))
        {
            return new EnqueueJobResult("Image_not_allowed");
        }
        var image = createJob.Image != string.Empty ? createJob.Image : conf.Image;

        var environments = new List<EnvVarInput>();
        foreach (var env in conf.Environments?.ToList() ?? [])
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

        List<string>? dependsOn = createJob.DependsOn ?? conf.DependsOn;

        CreateJob newCreateJob = new(
            createJob.Args,
            image,
            TtlSecondsAfterFinished: conf.TtlSecondsAfterFinished,
            Resources: JobResourceValidator.ValidateResources(conf.Resources,  createJob.Resources),
            Environments: environments,
            ConfigurationName: name,
            DependsOn: dependsOn);

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
