using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Kubernetes.Watch;
using SlimFaas.Options;

namespace SlimFaas.Jobs;


public class SlimJobsWorker(
    IJobQueue jobQueue,
    IJobService jobService,
    IJobConfiguration jobConfiguration,
    ILogger<SlimJobsWorker> logger,
    HistoryHttpMemoryService historyHttpService,
    ISlimDataStatus slimDataStatus,
    IMasterService masterService,
    IReplicasService replicasService,
    IOptions<WorkersOptions> workersOptions,
    IOptions<SlimFaasOptions> slimFaasOptions,
    KubernetesWatchSignals watchSignals)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.JobsDelayMilliseconds;

    private readonly TimeSpan _jobsResyncInterval =
        TimeSpan.FromSeconds(slimFaasOptions.Value.KubernetesWatch.JobsResyncSeconds);

    // Version -1 : la première itération synchronise toujours (état initial).
    private long _observedJobsVersion = -1;
    private DateTime _lastJobsSyncUtc = DateTime.MinValue;

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
            // La boucle reste cadencée à 1 s : elle traite aussi les queues SlimData
            // (DoJobOneCycle), indépendantes des événements Kubernetes. Seule la
            // synchronisation de la liste des jobs devient pilotée par les événements.
            await Task.Delay(_delay, stoppingToken);

            IList<Job> jobs;
            long version = watchSignals.Jobs.Version;
            // Sans watch, ou tant qu'un flux (jobs/pods) est indisponible, la liste est
            // resynchronisée à chaque cycle comme historiquement : le master ne doit
            // jamais compter les slots d'exécution sur une liste potentiellement périmée.
            bool syncDue = !watchSignals.WatchEnabled
                           || !watchSignals.Jobs.IsHealthy
                           || version != _observedJobsVersion
                           || DateTime.UtcNow - _lastJobsSyncUtc >= _jobsResyncInterval;
            if (syncDue)
            {
                // Version capturée AVANT la sync : un événement pendant la sync
                // déclenche la synchronisation du cycle suivant.
                jobs = await jobService.SyncJobsAsync();
                _observedJobsVersion = version;
                _lastJobsSyncUtc = DateTime.UtcNow;
            }
            else
            {
                // Sûr : SyncJobsAsync publie la liste via Interlocked.Exchange.
                jobs = jobService.Jobs;
            }

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

    internal async Task DoJobOneCycle(IList<Job> jobs)
    {
        try
        {
            // Finished jobs remain listed until TTL cleanup, but no longer reserve
            // execution slots or keep their dependencies awake.
            jobs = jobs.Where(j => j.Status is JobStatus.Pending or JobStatus.Running).ToList();
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
                    if (DependencyReference.TryGetLocalProcessName(dependOn, out _))
                        continue;
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

                var numberJobReady = ShouldWaitDependencies(count);
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

    private int ShouldWaitDependencies(IList<QueueData> countElement)
    {
        var numberPodReady = 0;
        DeploymentsInformations deployments = replicasService.Deployments;
        for (var i = countElement.Count - 1; i >= 0; i--)
        {
            var jobElement = countElement[i];
            JobInQueue? jobInQueue = MemoryPackSerializer.Deserialize<JobInQueue>(jobElement.Data);
            CreateJob? createJob = jobInQueue?.CreateJob;
            numberPodReady += 1;
            if (createJob?.DependsOn != null)
            {
                foreach (var dependOn in createJob.DependsOn)
                {
                    if (!DependencyReference.TryGetLocalProcessName(dependOn, out _))
                        historyHttpService.SetTickLastCall(dependOn, DateTime.UtcNow.Ticks);

                    if (!IsDependencyReady(deployments, dependOn))
                        numberPodReady = 0;
                }
            }
        }

        return numberPodReady;
    }

    internal static bool IsDependencyReady(
        DeploymentsInformations deployments,
        string dependency)
    {
        if (DependencyReference.TryGetLocalProcessName(dependency, out _))
            return DependencyReference.IsLocalProcessReady(deployments, dependency);

        DeploymentInformation? function =
            deployments.Functions.FirstOrDefault(item => item.Deployment == dependency);
        return function is not { Replicas: <= 0 };
    }
}
