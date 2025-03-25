using MemoryPack;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas.Jobs;


public class SlimJobsWorker(IJobQueue jobQueue, IJobService jobService,
    IJobConfiguration jobConfiguration, ILogger<SlimJobsWorker> logger,
    HistoryHttpMemoryService historyHttpService,
        ISlimDataStatus slimDataStatus,
        IMasterService masterService,
    IReplicasService replicasService,
        int delay = EnvironmentVariables.SlimJobsWorkerDelayMillisecondsDefault)
    : BackgroundService
{
    private readonly int _delay =
        EnvironmentVariables.ReadInteger(logger, EnvironmentVariables.SlimJobsWorkerDelayMilliseconds, delay);

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
            await Task.Delay(_delay, stoppingToken);

            var jobs = await jobService.SyncJobsAsync();

            if (!masterService.IsMaster)
            {
                return;
            }
            var failedJobs = jobs.Where(j => j.Status == JobStatus.Succeeded).ToList();
            foreach (var failedJob in failedJobs)
            {
                try {
                    logger.LogInformation("Delete job {JobName}", failedJob.Name);
                    await jobService.DeleteJobAsync(failedJob.Name);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error in SlimJobsWorker to delete job {JobName}", failedJob.Name);
                }
            }

            jobs = jobs.Where(j => j.Status == JobStatus.Pending && j.Status == JobStatus.Running).ToList();
            var jobsDictionary = new Dictionary<string, List<Job>>();
            var configurations = jobConfiguration.Configuration.Configurations;
            foreach (var data in configurations)
            {
                jobsDictionary.Add(data.Key, new List<Job>());
            }

            foreach (Job job in jobs.Where(j => j.Name.Contains(KubernetesService.SlimfaasJobKey)))
            {
                logger.LogInformation("Job {JobName} is running", job.Name);
                var jobNameSplits = job.Name.Split(KubernetesService.SlimfaasJobKey);
                string jobConfigurationName = jobNameSplits[0];

                if (configurations.TryGetValue(jobConfigurationName, out SlimfaasJob? configuration))
                {
                    if (configuration.DependsOn != null)
                    {
                        foreach (var dependOn in configuration.DependsOn)
                        {
                            historyHttpService.SetTickLastCall(dependOn, DateTime.UtcNow.Ticks);
                        }
                    }
                }

                if (jobsDictionary.ContainsKey(jobConfigurationName))
                {
                    jobsDictionary[jobConfigurationName].Add(job);
                }
            }

            foreach (var jobsKeyPairValue in jobsDictionary)
            {
                var jobList = jobsKeyPairValue.Value;
                var jobName = jobsKeyPairValue.Key;
                var numberElementToDequeue = configurations[jobsKeyPairValue.Key].NumberParallelJob - jobList.Count;
                if (numberElementToDequeue <= 0)
                {
                    continue;
                }

                var count = await jobQueue.CountElementAsync(jobName, new List<CountType> { CountType.Available },
                    int.MaxValue);
                if (count == 0)
                {
                    continue;
                }

                bool requiredToWait = await ShouldWaitDependencies(jobName, configurations, jobsKeyPairValue);
                if (requiredToWait)
                {
                    continue;
                }

                var elements = await jobQueue.DequeueAsync(jobName, numberElementToDequeue);
                if (elements == null || elements.Count == 0) continue;

                var listCallBack = new ListQueueItemStatus();
                listCallBack.Items = new List<QueueItemStatus>();
                foreach (QueueData element in elements)
                {
                    CreateJob? createJob = MemoryPackSerializer.Deserialize<CreateJob>(element.Data);
                    if (createJob == null)
                    {
                        continue;
                    }

                    try
                    {
                        await jobService.CreateJobAsync(jobName, createJob);
                        listCallBack.Items.Add(new QueueItemStatus(element.Id, 200));
                    }
                    catch (Exception e)
                    {
                        listCallBack.Items.Add(new QueueItemStatus(element.Id, 500));
                        logger.LogError(e, "Error in SlimJobsWorker");
                    }
                }

                if (listCallBack.Items.Count > 0)
                {
                    await jobQueue.ListCallbackAsync(jobName, listCallBack);
                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Global Error in SlimFaas Worker");
        }
    }

    private async Task<bool> ShouldWaitDependencies(string jobName, IDictionary<string, SlimfaasJob> configurations, KeyValuePair<string, List<Job>> jobsKeyPairValue)
    {
        var count = await jobQueue.CountElementAsync(jobName, new List<CountType> { CountType.Available }, int.MaxValue);
        if (count > 0)
        {
            var dependsOn = configurations[jobsKeyPairValue.Key].DependsOn;
            if (dependsOn != null)
            {
                foreach (var dependOn in dependsOn)
                {
                    historyHttpService.SetTickLastCall(dependOn, DateTime.UtcNow.Ticks);
                }
                foreach (var dependOn in dependsOn)
                {
                    var function = replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == dependOn);
                    if(function is { Replicas: <= 0 })
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
