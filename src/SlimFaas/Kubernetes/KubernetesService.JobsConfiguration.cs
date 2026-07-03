using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace SlimFaas.Kubernetes;

public partial class KubernetesService
{
    public async Task<SlimFaasJobConfiguration?> ListJobsConfigurationAsync(string kubeNamespace)
    {
        try
        {
            k8s.Kubernetes client = _client;

            Task<V1CronJobList>? cronJobListTask = client.ListNamespacedCronJobAsync(kubeNamespace);

            V1CronJobList? cronJobList = await cronJobListTask;

            return ExtractJobConfigurations(cronJobList);
        }
        catch (HttpOperationException e)
        {
            _logger.LogError(e, "Error while listing kubernetes cron jobs");
            return null;
        }
    }

    private SlimFaasJobConfiguration? ExtractJobConfigurations(V1CronJobList cronJobList)
    {
        Dictionary<string, SlimfaasJob> jobs = new();
        Dictionary<string, IList<ScheduleCreateJob>> schedules = new(StringComparer.OrdinalIgnoreCase);

        foreach (V1CronJob? cronJob in cronJobList.Items)
        {
            if (cronJob is null)
            {
                continue;
            }

            var annotations = cronJob.Metadata?.Annotations ?? new Dictionary<string, string>();
            V1Container? container = cronJob.Spec.JobTemplate.Spec.Template.Spec.Containers.FirstOrDefault();

            bool isSlimfaasJob = annotations.TryGetValue(Job, out var labelValue) &&
                                 bool.TryParse(labelValue, out var isJob) && isJob;
            if (!isSlimfaasJob)
                continue;

            string name = cronJob.Metadata?.Name ?? "unknown";
            bool suspend = cronJob.Spec.Suspend ?? false;

            if (!suspend)
            {
                _logger.LogWarning("CronJob {CronJobName} is not suspended, skipping it in the SlimFaas job configuration.", name);
                continue;
            }


            var image = container?.Image ?? "";

            var imagesWhitelist = annotations.TryGetValue(JobImagesWhitelist, out var whitelist)
                ? whitelist.Split(',').Select(s => s.Trim()).ToList()
                : new List<string>();

            if (!string.IsNullOrEmpty(image) && !imagesWhitelist.Contains(image))
            {
                imagesWhitelist.Add(image);
            }

            CreateJobResources? resources = new (
                container?.Resources.Requests?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ??
                new Dictionary<string, string> { { "cpu", "100m" }, { "memory", "100Mi" } },
                container?.Resources.Limits?.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()) ??
                new Dictionary<string, string> { { "cpu", "100m" }, { "memory", "100Mi" } }
            );

            List<string>? dependsOn = annotations.TryGetValue(DependsOn, out var dependsOnValue)
                ? dependsOnValue.Split(',').Select(s => s.Trim()).ToList()
                : null;

            List<EnvVarInput>? environments = null;

            if (container?.Env != null)
            {
                environments = container.Env?.Select(e => new EnvVarInput(
                    Name: e.Name,
                    Value: e.Value ?? "",
                    SecretRef: e.ValueFrom?.SecretKeyRef != null
                        ? new SecretRef(e.ValueFrom.SecretKeyRef.Name, e.ValueFrom.SecretKeyRef.Key)
                        : null,
                    ConfigMapRef: e.ValueFrom?.ConfigMapKeyRef != null
                        ? new ConfigMapRef(e.ValueFrom.ConfigMapKeyRef.Name, e.ValueFrom.ConfigMapKeyRef.Key)
                        : null,
                    FieldRef: e.ValueFrom?.FieldRef != null
                        ? new FieldRef(e.ValueFrom.FieldRef.FieldPath)
                        : null,
                    ResourceFieldRef: e.ValueFrom?.ResourceFieldRef != null
                        ? new ResourceFieldRef(
                            e.ValueFrom.ResourceFieldRef.ContainerName,
                            e.ValueFrom.ResourceFieldRef.Resource,
                            e.ValueFrom.ResourceFieldRef.Divisor?.ToString() ?? "")
                        : null
                )).ToList();
            }

            if (container?.EnvFrom != null)
            {
                var envFromList = container.EnvFrom
                    .Select(e =>
                    {
                        if (e.SecretRef != null)
                        {
                            return new EnvVarInput(
                                Name: (e.Prefix ?? "") + "*",
                                Value: "",
                                SecretRef: new SecretRef(e.SecretRef.Name, "*")
                            );
                        }

                        if (e.ConfigMapRef != null)
                        {
                            return new EnvVarInput(
                                Name: (e.Prefix ?? "") + "*",
                                Value: "",
                                ConfigMapRef: new ConfigMapRef(e.ConfigMapRef.Name, "*")
                            );
                        }

                        return null;
                    })
                    .Where(e => e != null)
                    .Cast<EnvVarInput>()
                    .ToList();

                environments = (environments ?? new List<EnvVarInput>()).Concat(envFromList).ToList();
            }

            var visibility = annotations.TryGetValue(DefaultVisibility, out var vis) ? vis : nameof(FunctionVisibility.Private);
            var numberParallel = Int32.TryParse(annotations.TryGetValue(NumberParallelJob, out var par) ? par : "1", out var result) ? result : 1;

            var restartPolicy = cronJob.Spec.JobTemplate.Spec.Template.Spec.RestartPolicy ?? "Never";
            var backOffLimit = cronJob.Spec.JobTemplate.Spec.BackoffLimit ?? 1;
            var ttlSecondsAfterFinished = cronJob.Spec.JobTemplate.Spec.TtlSecondsAfterFinished ?? 60;

            jobs[name] = new SlimfaasJob(
                Image: image,
                ImagesWhitelist: imagesWhitelist,
                Resources: resources,
                DependsOn: dependsOn,
                Environments: environments,
                BackoffLimit: backOffLimit,
                Visibility: visibility,
                NumberParallelJob: numberParallel,
                TtlSecondsAfterFinished: ttlSecondsAfterFinished,
                RestartPolicy: restartPolicy
            );

            if (annotations.TryGetValue(JobSchedules, out var schedulesJson) && !string.IsNullOrWhiteSpace(schedulesJson))
            {
                try
                {
                    schedulesJson = JsonMinifier.MinifyJson(schedulesJson)!;
                    var parsedSchedules = JsonSerializer.Deserialize(
                        schedulesJson,
                        ScheduleCreateJobListSerializerContext.Default.ListScheduleCreateJob);

                    if (parsedSchedules is { Count: > 0 })
                    {
                        schedules[name] = parsedSchedules.Select(s => new ScheduleCreateJob(
                            Schedule: s.Schedule,
                            Args: s.Args,
                            Image: string.IsNullOrEmpty(s.Image) ? image : s.Image,
                            BackoffLimit: s.BackoffLimit == 1 ? backOffLimit : s.BackoffLimit,
                            TtlSecondsAfterFinished: s.TtlSecondsAfterFinished == 60 ? ttlSecondsAfterFinished : s.TtlSecondsAfterFinished,
                            RestartPolicy: s.RestartPolicy == "Never" ? restartPolicy : s.RestartPolicy,
                            Resources: s.Resources ?? resources,
                            Environments: s.Environments ?? environments,
                            DependsOn: s.DependsOn ?? dependsOn
                        )).ToList();
                    }
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error parsing SlimFaas/Schedules annotation for CronJob {CronJobName}", name);
                }
            }

            _logger.LogDebug("JobConfiguration: ");
            _logger.LogDebug(jobs[name].ToString());
        }

        if (jobs.Count != 0)
        {
            return new SlimFaasJobConfiguration(jobs, schedules.Count > 0 ? schedules : null);
        }

        _logger.LogDebug("No SlimFaas job configurations found in the cluster.");

        return null;
    }
}
