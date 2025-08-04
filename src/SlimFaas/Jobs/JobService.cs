using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MemoryPack;
using SlimData;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;

public interface IJobService
{
    Task CreateJobAsync(string name, CreateJob createJob, string elementId, string jobFullName, long inQueueTimestamp);
    Task<bool> DeleteJobAsync(string name, string elementId, bool isMessageComeFromNamespaceInternal);
    Task<IList<Job>> SyncJobsAsync();
    IList<Job> Jobs { get; }
    Task<EnqueueJobResult> EnqueueJobAsync(string name, CreateJob createJob, bool isMessageComeFromNamespaceInternal);

    Task<IList<JobListResult>> ListJobAsync(string jobName);
}

[MemoryPackable]
public partial record JobInQueue(CreateJob CreateJob, string JobFullName, long InQueueTimestamp);

public record EnqueueJobResult(string ErrorKey, string ElementId, int Code=400);

public record EnqueueJobResultSuccess(string Id);

[JsonSerializable(typeof(EnqueueJobResultSuccess))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class EnqueueJobResultSuccessSerializerContext : JsonSerializerContext;

public enum JobStatusResult
{
    Queued =40,
}


public record JobListResult(string Name, string Status, string Id, int PositionInQueue=-1, long InQueueTimestamp=0, long StartTimestamp=0);

[JsonSerializable(typeof(JobListResult))]
[JsonSerializable(typeof(IList<JobListResult>))]
[JsonSerializable(typeof(List<JobListResult>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class JobListResultSerializerContext : JsonSerializerContext;


public class JobService(IKubernetesService kubernetesService, IJobConfiguration jobConfiguration, IJobQueue jobQueue) : IJobService
{
    private const string Default = "Default";

    private readonly string _namespace = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ??
                                         EnvironmentVariables.NamespaceDefault;

    public async Task CreateJobAsync(string name, CreateJob createJob, string elementId, string jobFullName, long inQueueTimestamp)
    {
        await kubernetesService.CreateJobAsync(_namespace, name, createJob, elementId, jobFullName, inQueueTimestamp);
    }

    private static string ConvertPatternToRegex(string pattern)
    {
        return "^" + Regex.Escape(pattern)
                       .Replace("\\*", ".*")  // '*' devient '.*'
                       .Replace(":", "\\:")    // Échapper les deux-points
                   + "$";
    }

    private static bool IsPatternMatch(string pattern, string target)
    {
        string regexPattern = ConvertPatternToRegex(pattern);
        TimeSpan timeout = TimeSpan.FromSeconds(2);

        try
        {
            return Regex.IsMatch(target, regexPattern, RegexOptions.None, timeout);
        }
        catch (RegexMatchTimeoutException)
        {
            Console.WriteLine($"Error: regex job pattern {pattern} generate a timeout");
            return false;
        }
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
        name = configuration.ContainsKey(name) ? name : Default;
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return new EnqueueJobResult("Visibility_private", string.Empty, 400);
        }

        if (createJob.Image != string.Empty && !IsImageAllowed(conf.ImagesWhitelist, createJob.Image))
        {
            return new EnqueueJobResult("Image_not_allowed", string.Empty);
        }
        var image = createJob.Image != string.Empty ? createJob.Image : conf.Image;

        var environments = (conf.Environments?.ToList() ?? [])
            .Where(env => (createJob.Environments ?? new List<EnvVarInput>())
            .All(e => e.Name != env.Name))
            .ToList();
        environments.AddRange(createJob.Environments ?? new List<EnvVarInput>());

        List<string>? dependsOn = createJob.DependsOn ?? conf.DependsOn;

        CreateJob newCreateJob = new(
            createJob.Args,
            image,
            TtlSecondsAfterFinished: conf.TtlSecondsAfterFinished,
            Resources: JobResourceValidator.ValidateResources(conf.Resources,  createJob.Resources),
            Environments: environments,
            ConfigurationName: name,
            DependsOn: dependsOn);

        string fullName = $"{name}{KubernetesService.SlimfaasJobKey}{TinyGuid.NewTinyGuid()}";
        JobInQueue createJobInQueue = new(newCreateJob, fullName, DateTime.UtcNow.Ticks);
        var createJobSerialized = MemoryPackSerializer.Serialize(createJobInQueue);
        var elementId = await jobQueue.EnqueueAsync(name, createJobSerialized);

        return new EnqueueJobResult(string.Empty, elementId, 202);
    }

    private IList<Job> _jobs = new List<Job>();

    public IList<Job> Jobs => new List<Job>(Volatile.Read(ref _jobs).ToArray());

    public async Task<IList<Job>> SyncJobsAsync()
    {
        var jobs = await kubernetesService.ListJobsAsync(_namespace);
        Interlocked.Exchange(ref _jobs, jobs);
        return jobs;
    }

    public async Task<IList<JobListResult>> ListJobAsync(string jobName)
    {
        var countElement = await jobQueue.CountElementAsync(jobName, new List<CountType> { CountType.Available });
        var result = new List<JobListResult>();

        var index = -1;
        foreach (var job in _jobs.OrderByDescending(j => j.InQueueTimestamp))
        {
            result.Add(new JobListResult(job.Name, job.Status.ToString(), job.ElementId, index, job.InQueueTimestamp, job.StartTimestamp));
            index--;
        }

        index = 0;
        foreach (QueueData queueData in countElement)
        {
            JobInQueue? jobInQueue = MemoryPackSerializer.Deserialize<JobInQueue>(queueData.Data);
            result.Add(new JobListResult(jobInQueue?.JobFullName ?? "", nameof(JobStatusResult.Queued), queueData.Id, index, jobInQueue?.InQueueTimestamp ?? 0, -1));
            index++;
        }

        return result;
    }

    public async Task<bool> DeleteJobAsync(string name, string elementId, bool isMessageComeFromNamespaceInternal)
    {
        var configuration = jobConfiguration.Configuration.Configurations;
        name = configuration.ContainsKey(name) ? name : Default;
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return false;
        }

        var countElement = await jobQueue.CountElementAsync(name, new List<CountType> { CountType.Available });
        var element = countElement.FirstOrDefault(e => e.Id == elementId);
        if (element != null)
        {
            List<QueueItemStatus> items =
            [
                new() { Id = elementId, HttpCode = SlimDataInterpreter.DeleteFromQueueCode }
            ];
            await jobQueue.ListCallbackAsync(name, new ListQueueItemStatus { Items = items });
            return true;
        }

        var job = _jobs.FirstOrDefault(j => j.ElementId == elementId);

        if (job == null)
        {
            return false;
        }

        await kubernetesService.DeleteJobAsync(_namespace, job.Name);

        return true;
    }
}
