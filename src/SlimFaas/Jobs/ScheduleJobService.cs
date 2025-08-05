using MemoryPack;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;


public record CreateScheduleJobResult(string Id);

public interface IScheduleJobService
{
    public Task<ResultWithError<CreateScheduleJobResult>> CreateScheduleJobAsync(string name, ScheduleCreateJob createJob,
        bool isMessageComeFromNamespaceInternal);
}

public class ScheduleJobService(IJobConfiguration jobConfiguration, IDatabaseService databaseService) : IScheduleJobService
{

    public const string ScheduleJob = "ScheduleJob:";
    public async Task<ResultWithError<CreateScheduleJobResult>> CreateScheduleJobAsync(string name, ScheduleCreateJob createJob, bool isMessageComeFromNamespaceInternal)
    {
        var timeStamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var result = Cron.GetLatestJobExecutionTimestamp(createJob.Schedule, timeStamp);
        if (!result.IsSuccess)
        {
            return new ResultWithError<CreateScheduleJobResult>(null , result.Error);
        }

        var configuration = jobConfiguration.Configuration.Configurations;
        name = configuration.ContainsKey(name) ? name : JobService.Default;
        var conf = configuration[name];
        if (!isMessageComeFromNamespaceInternal && conf.Visibility == nameof(FunctionVisibility.Private))
        {
            return new ResultWithError<CreateScheduleJobResult>(null , new ErrorResult("visibility_private"));
        }

        if (createJob.Image != string.Empty && !JobService.IsImageAllowed(conf.ImagesWhitelist, createJob.Image))
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

}
