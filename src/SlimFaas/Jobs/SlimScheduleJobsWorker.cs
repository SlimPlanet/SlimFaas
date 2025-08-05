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
                   var executionKey = $"{ScheduleJobService.ScheduleJob}{keyValue.Key}:{id}";
                   var timeStamp = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
                   var lastestExecutionTimeStampFromDatabaseBytes = await databaseService.GetAsync(id);
                   if (lastestExecutionTimeStampFromDatabaseBytes == null)
                   {
                       await databaseService.SetAsync(executionKey, MemoryPackSerializer.Serialize(timeStamp));
                       continue;
                   }
                   var lastestExecutionTimeStampFromDatabase = MemoryPackSerializer.Deserialize<long>(lastestExecutionTimeStampFromDatabaseBytes);
                   var cronSchedule = scheduleConfiguration.Schedule;
                   var latestExecutionTimeStamp = Cron.GetLatestJobExecutionTimestamp(cronSchedule, timeStamp).Data;

                   bool runJob = latestExecutionTimeStamp > lastestExecutionTimeStampFromDatabase;
                   if (!runJob)
                   {
                       continue;
                   }

                   var result = await jobService.EnqueueJobAsync(keyValue.Key,
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
                   }
               }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Global error in SlimFaas schedule jobs worker");
        }
    }

}
