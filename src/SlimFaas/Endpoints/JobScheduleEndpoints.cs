using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;

namespace SlimFaas.Endpoints;

public class JobSchedule
{

}

public static class JobScheduleEndpoints
{
    public static void MapJobScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /job-schedules/{functionName} - Créer un job planifié
        app.MapPost("/job-schedules/{functionName}", CreateScheduleJob)
            .WithName("CreateScheduleJob")
            .Produces<CreateScheduleJobResult>(201)
            .Produces(400)
            .Produces(500)
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();

        // GET /job-schedules/{functionName} - Lister les jobs planifiés
        app.MapGet("/job-schedules/{functionName}", ListScheduleJobs)
            .WithName("ListScheduleJobs")
            .Produces<IList<ListScheduleJob>>(200)
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();

        // DELETE /job-schedules/{functionName}/{elementId} - Supprimer un job planifié
        app.MapDelete("/job-schedules/{functionName}/{elementId}", DeleteScheduleJob)
            .WithName("DeleteScheduleJob")
            .Produces(204)
            .Produces(404)
            .Produces(400)
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();

        // Bloquer PUT et PATCH
        app.MapMethods("/job-schedules/{functionName}", new[] { "PUT", "PATCH" },
            () => Results.StatusCode((int)HttpStatusCode.MethodNotAllowed))
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();
    }

    private static async Task<IResult> CreateScheduleJob(
        string functionName,
        HttpContext context,
        [FromServices] IScheduleJobService? scheduleJobService,
        [FromServices] IFunctionAccessPolicy accessPolicy,
        [FromServices] ILogger<JobSchedule> logger)
    {
        if (scheduleJobService == null)
        {
            return Results.NotFound();
        }

        ScheduleCreateJob? scheduleCreateJob = await context.Request.ReadFromJsonAsync(
            ScheduleCreateJobSerializerContext.Default.ScheduleCreateJob);

        if (scheduleCreateJob == null)
        {
            return Results.BadRequest();
        }

        functionName = functionName.ToLowerInvariant();
        logger.LogInformation("Create job {JobName} with {ScheduleCreateJob}", functionName, scheduleCreateJob);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Create job details {ScheduleCreateJob} ",
                JsonSerializer.Serialize(scheduleCreateJob,
                    ScheduleCreateJobSerializerContext.Default.ScheduleCreateJob));
        }

        bool isInternal = accessPolicy.IsInternalRequest(context);

        var result = await scheduleJobService.CreateScheduleJobAsync(functionName, scheduleCreateJob, isInternal);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Job HTTP Status {HttpStatusCode} with error {ErrorKey}",
                400, result.Error?.Key ?? "");
            return Results.BadRequest();
        }

        if (result.Data == null)
        {
            return Results.StatusCode((int)HttpStatusCode.InternalServerError);
        }

        return Results.Json(result.Data,
            CreateScheduleJobResultSerializerContext.Default.CreateScheduleJobResult,
            statusCode: (int)HttpStatusCode.Created);
    }

    private static async Task<IResult> ListScheduleJobs(
        string functionName,
        [FromServices] IScheduleJobService? scheduleJobService)
    {
        if (scheduleJobService == null)
        {
            return Results.NotFound();
        }

        var jobs = await scheduleJobService.ListScheduleJobAsync(functionName);
        return Results.Json(jobs, ListScheduleJobSerializerContext.Default.IListListScheduleJob);
    }

    private static async Task<IResult> DeleteScheduleJob(
        string functionName,
        string elementId,
        HttpContext context,
        [FromServices] IScheduleJobService? scheduleJobService,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService,
        [FromServices] ILogger<JobSchedule> logger)
    {
        if (scheduleJobService == null)
        {
            return Results.NotFound();
        }

        bool isMessageComeFromNamespaceInternal =
            FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService);

        logger.LogInformation("Delete job schedule {JobName} with {Id}", functionName, elementId);

        var result = await scheduleJobService.DeleteScheduleJobAsync(
            functionName, elementId, isMessageComeFromNamespaceInternal);

        if (result.IsSuccess)
        {
            return Results.NoContent();
        }

        if (result.Error?.Key == ScheduleJobService.NotFound)
        {
            return Results.NotFound();
        }

        return Results.BadRequest();
    }
}

