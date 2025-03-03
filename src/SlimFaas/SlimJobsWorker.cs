using MemoryPack;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;

namespace SlimFaas;


public class SlimJobsWorker(IJobQueue jobQueue, IJobService jobService,
    JobConfiguration jobConfiguration, ILogger<SlimJobsWorker> logger,
        IServiceProvider serviceProvider,
        ISlimDataStatus slimDataStatus,
        IMasterService masterService,
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
            if (masterService.IsMaster)
            {
                var jobsDictionary = new Dictionary<string, List<Job>>();
                foreach (Job job in jobs.Where(j => j.Name.Contains(KubernetesService.SlimfaasJobKey)))
                {
                    var jobNameSplits = job.Name.Split(KubernetesService.SlimfaasJobKey);
                    string jobConfigurationName = jobNameSplits[0];
                    if (jobsDictionary.ContainsKey(jobConfigurationName))
                    {
                        jobsDictionary[jobConfigurationName].Add(job);
                    }
                    else
                    {
                        jobsDictionary.Add(jobConfigurationName, [job]);
                    }
                }

                foreach (var jobsKeyPairValue in jobsDictionary)
                {
                    var jobList = jobsKeyPairValue.Value;
                    var jobName = jobsKeyPairValue.Key;
                    var numberElementToDequeue = jobConfiguration.Configuration.Configurations[jobsKeyPairValue.Key].NumberParallelRequest - jobList.Count;
                    if (numberElementToDequeue > 0)
                    {
                        var elements = await jobQueue.DequeueAsync(jobName, numberElementToDequeue);
                        if(elements == null) continue;

                        foreach (QueueData element in elements)
                        {
                            CreateJob? createJob = MemoryPackSerializer.Deserialize<CreateJob>(element.Data);
                            if (createJob == null)
                            {
                                continue;
                            }

                            await jobService.CreateJobAsync(jobName, createJob);
                        }

                    }

                }
            }
        }
        catch (Exception e)
        {
            logger.LogError(e, "Global Error in SlimFaas Worker");
        }
    }

}
