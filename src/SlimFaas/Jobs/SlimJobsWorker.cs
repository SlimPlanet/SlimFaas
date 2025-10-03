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
            await DoJobOneCycle(jobs);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Global error in slimFaas jobs worker");
        }
    }

    private async Task DoJobOneCycle(IList<Job> jobs)
    {
        try
        {
            jobs = jobs.Where(j => j.Status != JobStatus.ImagePullBackOff).ToList();
            var jobsDictionary = new Dictionary<string, List<Job>>(StringComparer.OrdinalIgnoreCase);
            var configurations = jobConfiguration.Configuration.Configurations;
            foreach (var data in configurations)
            {
                jobsDictionary.Add(data.Key.ToLowerInvariant(), new List<Job>());
            }

            foreach (Job job in jobs.Where(j => j.Name.Contains(KubernetesService.SlimfaasJobKey)))
            {
                var jobNameSplits = job.Name.Split(KubernetesService.SlimfaasJobKey);
                string jobConfigurationName = jobNameSplits[0];

                foreach (var dependOn in job.DependsOn)
                {
                    historyHttpService.SetTickLastCall(dependOn, DateTime.UtcNow.Ticks);
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

                var count = await jobQueue.CountElementAsync(jobName, new List<CountType> { CountType.Available });
                if (count.Count == 0)
                {
                    continue;
                }

                var numberJobReady = await ShouldWaitDependencies(jobName);
                if (numberJobReady<=0)
                {
                    continue;
                }

                var elements = await jobQueue.DequeueAsync(jobName, Math.Min(numberJobReady, numberElementToDequeue));
                if (elements == null || elements.Count == 0) continue;

                var listCallBack = new ListQueueItemStatus();
                listCallBack.Items = new List<QueueItemStatus>();
                foreach (QueueData element in elements)
                {
                    JobInQueue? jobInQueue = MemoryPackSerializer.Deserialize<JobInQueue>(element.Data);

                    if (jobInQueue == null)
                    {
                        continue;
                    }
                    CreateJob createJob = jobInQueue.CreateJob;
                    try
                    {
                        await jobService.CreateJobAsync(jobName, createJob, element.Id, jobInQueue.JobFullName, jobInQueue.InQueueTimestamp);
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
            logger.LogError(e, "Job worker error");
        }
    }

    private async Task<int> ShouldWaitDependencies(string jobName)
    {
        var numberPodReady = 0;
        var countElement = await jobQueue.CountElementAsync(jobName, new List<CountType> { CountType.Available });
        if (countElement.Count > 0)
        {
            var reversedJobElement = countElement.Reverse().ToList();
            foreach (var jobElement in reversedJobElement)
            {
                JobInQueue? jobInQueue = MemoryPackSerializer.Deserialize<JobInQueue>(jobElement.Data);
                CreateJob? createJob = jobInQueue?.CreateJob;
                numberPodReady += 1;
                if (createJob?.DependsOn != null)
                {
                    foreach (var dependOn in createJob.DependsOn)
                    {
                        historyHttpService.SetTickLastCall(dependOn, DateTime.UtcNow.Ticks);

                        var function =
                            replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == dependOn);
                        if (function is { Replicas: <= 0 })
                        {
                            numberPodReady = 0;
                        }
                    }
                }
            }
        }

        return numberPodReady;
    }
}
