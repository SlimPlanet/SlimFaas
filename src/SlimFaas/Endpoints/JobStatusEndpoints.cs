using MemoryPack;
using Microsoft.AspNetCore.Mvc;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Endpoints;

public static class JobStatusEndpoints
{
    public static void MapJobStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/jobs/status", GetAllJobStatuses)
            .WithName("GetAllJobStatuses")
            .Produces<List<JobConfigurationStatus>>(200)
            .AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static async Task<IResult> GetAllJobStatuses(
        HttpContext context,
        [FromServices] IJobConfiguration jobConfiguration,
        [FromServices] IJobService jobService,
        [FromServices] IScheduleJobService? scheduleJobService,
        [FromServices] IDatabaseService databaseService)
    {
        var configurations = jobConfiguration.Configuration.Configurations;
        var schedules = jobConfiguration.Configuration.Schedules;
        var currentJobs = jobService.Jobs;
        long nowUnix = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();

        var result = new List<JobConfigurationStatus>();

        foreach (var (name, conf) in configurations)
        {
            // Running / queued jobs for this configuration
            var running = currentJobs
                .Where(j => j.Name.StartsWith(name + KubernetesService.SlimfaasJobKey, StringComparison.OrdinalIgnoreCase)
                            || j.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                .Select(j => new RunningJobStatus(
                    j.Name,
                    j.Status.ToString(),
                    j.ElementId,
                    j.InQueueTimestamp,
                    j.StartTimestamp))
                .ToList();

            // Scheduled jobs: from Kubernetes annotations + from API (database)
            var scheduledInfos = new List<ScheduledJobInfo>();

            // 1) Kubernetes-configured schedules
            if (schedules != null && schedules.TryGetValue(name, out var k8sSchedules))
            {
                foreach (var s in k8sSchedules)
                {
                    long? next = null;
                    var nextResult = Cron.GetNextJobExecutionTimestamp(s.Schedule, nowUnix);
                    if (nextResult.IsSuccess) next = nextResult.Data;

                    var id = IdGenerator.GetId32Hex(MemoryPackSerializer.Serialize(s));
                    scheduledInfos.Add(new ScheduledJobInfo(
                        id, s.Schedule, s.Image, next, s.Resources, s.DependsOn));
                }
            }

            // 2) API-configured schedules (database)
            if (scheduleJobService != null)
            {
                var apiSchedules = await scheduleJobService.ListScheduleJobAsync(name);
                foreach (var s in apiSchedules)
                {
                    long? next = null;
                    var nextResult = Cron.GetNextJobExecutionTimestamp(s.Schedule, nowUnix);
                    if (nextResult.IsSuccess) next = nextResult.Data;

                    scheduledInfos.Add(new ScheduledJobInfo(
                        s.Id, s.Schedule, s.Image, next, s.Resources, s.DependsOn));
                }
            }

            result.Add(new JobConfigurationStatus(
                Name: name,
                Visibility: conf.Visibility,
                Image: conf.Image,
                ImagesWhitelist: conf.ImagesWhitelist,
                NumberParallelJob: conf.NumberParallelJob,
                Resources: conf.Resources,
                DependsOn: conf.DependsOn,
                Schedules: scheduledInfos,
                RunningJobs: running));
        }

        return Results.Json(result,
            ListJobConfigurationStatusSerializerContext.Default.ListJobConfigurationStatus);
    }
}

