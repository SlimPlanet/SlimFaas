using System.Text.Json.Serialization;
using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;


public record CreateScheduleJobResult(string Id);

[JsonSerializable(typeof(CreateScheduleJobResult))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class CreateScheduleJobResultSerializerContext : JsonSerializerContext;


[MemoryPackable]
public partial record ListScheduleJob(
    string Id,
    string Schedule,
    List<string> Args,
    string Image = "",
    int BackoffLimit = 1,
    int TtlSecondsAfterFinished = 60,
    string RestartPolicy = "Never",
    CreateJobResources? Resources = null,
    IList<EnvVarInput>? Environments = null,
    List<string>? DependsOn = null);

[JsonSerializable(typeof(IList<ListScheduleJob>))]
[JsonSerializable(typeof(List<ListScheduleJob>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class ListScheduleJobSerializerContext : JsonSerializerContext;

public interface IScheduleJobService
{
    public Task<ResultWithError<CreateScheduleJobResult>> CreateScheduleJobAsync(string name, ScheduleCreateJob createJob,
        bool isMessageComeFromNamespaceInternal);

    public Task<IList<ListScheduleJob>> ListScheduleJobAsync(string name);

    public Task<ResultWithError<string>> DeleteScheduleJobAsync(string name, string id, bool isMessageComeFromNamespaceInternal);
}

public class ScheduleJobService(
    IJobConfiguration jobConfiguration,
    IDatabaseService databaseService,
    IJobService jobService) : IScheduleJobService
{

    public const string ScheduleJob = "ScheduleJob:";
    public const string NotFound = "not_found";
    public async Task<ResultWithError<CreateScheduleJobResult>> CreateScheduleJobAsync(string name, ScheduleCreateJob createJob, bool isMessageComeFromNamespaceInternal)
    {
        var timeStamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var result = Cron.GetLatestJobExecutionTimestamp(createJob.Schedule, timeStamp);
        if (!result.IsSuccess)
        {
            return new ResultWithError<CreateScheduleJobResult>(null , result.Error);
        }

        var configuration = jobConfiguration.Configuration.Configurations;
        name = configuration.ContainsKey(name) ? name : JobConfiguration.Default;
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return new ResultWithError<CreateScheduleJobResult>(null , new ErrorResult("visibility_private"));
        }

        if (createJob.Image != string.Empty && !jobService.IsImageAllowed(conf.ImagesWhitelist, createJob.Image))
        {
            return new ResultWithError<CreateScheduleJobResult>(null , new ErrorResult("image_not_allowed"));
        }

        var idSchedule = Guid.NewGuid().ToString();
        byte[] memory = MemoryPackSerializer.Serialize(createJob);
        var dictionary = new Dictionary<string, byte[]>();
        dictionary.Add(idSchedule, memory);
        await databaseService.HashSetAsync($"{ScheduleJob}{name}", dictionary);
        return new ResultWithError<CreateScheduleJobResult>(new CreateScheduleJobResult(idSchedule));
    }


    public async Task<IList<ListScheduleJob>> ListScheduleJobAsync(string name)
    {
        var schedules = await databaseService.HashGetAllAsync($"{ScheduleJob}{name}");
        var results = new List<ListScheduleJob>();

        foreach (var schedule in schedules)
        {
            var value = MemoryPackSerializer.Deserialize<ScheduleCreateJob>(schedule.Value);
            if(value == null)
            {
                continue;
            }
            results.Add(new ListScheduleJob(
                schedule.Key,
                value.Schedule,
                value.Args,
                value.Image,
                value.BackoffLimit,
                value.TtlSecondsAfterFinished,
                value.RestartPolicy,
                value.Resources,
                value.Environments,
                value.DependsOn ?? new List<string>(0)
                ));
        }

        return results;
    }

    public async Task<ResultWithError<string>> DeleteScheduleJobAsync(string name, string id, bool isMessageComeFromNamespaceInternal)
    {
        var configuration = jobConfiguration.Configuration.Configurations;
        name = configuration.ContainsKey(name) ? name : JobConfiguration.Default;
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return new ResultWithError<string>(null , new ErrorResult("visibility_private"));
        }
        var schedules = await databaseService.HashGetAllAsync($"{ScheduleJob}{name}");
        if (!schedules.ContainsKey(id))
        {
            return new ResultWithError<string>(null, new ErrorResult(NotFound));
        }
        await databaseService.HashSetDeleteAsync($"{ScheduleJob}{name}",  id );

        return new ResultWithError<string>(id);
    }

}
