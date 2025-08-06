using MemoryPack;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;


public class SlimScheduleJobsWorker( IJobService jobService,
    IJobConfiguration jobConfiguration, ILogger<SlimJobsWorker> logger,
        ISlimDataStatus slimDataStatus,
        IDatabaseService databaseService,
        IMasterService masterService,
        int delay = 1000 * 60)
    : BackgroundService
{

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await slimDataStatus.WaitForReadyAsync();
        while (stoppingToken.IsCancellationRequested == false)
        {
            await DoOneCycle(stoppingToken);
        }
    }

    private async Task DoOneCycle(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);

            if (!masterService.IsMaster)
            {
                return;
            }

            foreach(var keyValue in jobConfiguration.Configuration.Configurations)
            {
               var hashSet = await databaseService.HashGetAllAsync(ScheduleJobService.ScheduleJob + keyValue.Key);
               foreach (KeyValuePair<string, byte[]> keyValuePair in hashSet)
               {
                   var id = keyValuePair.Key;
                   var scheduleConfiguration = MemoryPackSerializer.Deserialize<ScheduleCreateJob>(keyValuePair.Value);
                   if (scheduleConfiguration == null)
                   {
                       continue;
                   }
                   await EnqueueJobIfNeeded(keyValue.Key, id, scheduleConfiguration);
               }
            }

            if (jobConfiguration.Configuration.Schedules == null)
            {
                return;
            }

            foreach (KeyValuePair<string, IList<ScheduleCreateJob>> configurationSchedule in jobConfiguration.Configuration.Schedules)
            {
                foreach (ScheduleCreateJob scheduleCreateJob in configurationSchedule.Value)
                {
                    var id = IdGenerator.GetId32Hex(MemoryPackSerializer.Serialize(scheduleCreateJob));
                    await EnqueueJobIfNeeded(configurationSchedule.Key, id, scheduleCreateJob);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Global error in SlimFaas schedule jobs worker");
        }
    }

    private async Task EnqueueJobIfNeeded(string configurationName, string id, ScheduleCreateJob scheduleConfiguration)
    {
        var executionKey = $"{ScheduleJobService.ScheduleJob}{configurationName}:{id}";
        var timeStamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        var lastestExecutionTimeStampFromDatabaseBytes = await databaseService.GetAsync(id);
        if (lastestExecutionTimeStampFromDatabaseBytes == null)
        {
            await databaseService.SetAsync(executionKey, MemoryPackSerializer.Serialize(timeStamp));
            return;
        }
        var lastestExecutionTimeStampFromDatabase = MemoryPackSerializer.Deserialize<long>(lastestExecutionTimeStampFromDatabaseBytes);
        var cronSchedule = scheduleConfiguration.Schedule;
        var latestExecutionTimeStamp = Cron.GetLatestJobExecutionTimestamp(cronSchedule, timeStamp).Data;

        bool runJob = latestExecutionTimeStamp > lastestExecutionTimeStampFromDatabase;
        if (!runJob)
        {
            return;
        }

        var result = await jobService.EnqueueJobAsync(configurationName,
            new CreateJob(scheduleConfiguration.Args,
                scheduleConfiguration.Image,
                scheduleConfiguration.BackoffLimit,
                scheduleConfiguration.TtlSecondsAfterFinished,
                scheduleConfiguration.RestartPolicy,
                scheduleConfiguration.Resources,
                scheduleConfiguration.Environments,
                scheduleConfiguration.DependsOn), true);

        if (result.IsSuccess)
        {
            await databaseService.SetAsync(executionKey, MemoryPackSerializer.Serialize(latestExecutionTimeStamp));
            logger.LogInformation("Enqueued job for schedule {ScheduleId} in configuration {ConfigurationName}",
                id, configurationName);
        }
        else
        {
            logger.LogError("Failed to enqueue job for schedule {ScheduleId} in configuration {ConfigurationName}: {Error}",
                id, configurationName, result.Error?.Key);
        }
    }
}
