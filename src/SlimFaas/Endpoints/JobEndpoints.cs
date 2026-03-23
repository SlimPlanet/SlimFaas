using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Endpoints;

public class Job
{

}

public static partial class JobEndpoints
{
    [GeneratedRegex(@"^[a-z\-]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FunctionNamePattern();

    private static bool IsValidFunctionName(string functionName, ILogger logger)
    {
        if (functionName.Length < 3 || functionName.Length > 12 || !FunctionNamePattern().IsMatch(functionName))
        {
            logger.LogWarning("Invalid function name: {FunctionName}. Must match pattern [a-z-] and be between 3 and 12 characters", functionName);
            return false;
        }
        return true;
    }

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /job/{functionName} - Créer un job
        app.MapPost("/job/{functionName}", CreateJob)
            .WithName("CreateJob")
            .Produces<EnqueueJobResult>(202)
            .Produces(400)
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();

        // GET /job/{functionName} - Lister les jobs
        app.MapGet("/job/{functionName}", ListJobs)
            .WithName("ListJobs")
            .Produces<List<JobListResult>>(200)
            .AddEndpointFilter<HostPortEndpointFilter>();

        // DELETE /job/{functionName}/{elementId} - Supprimer un job
        app.MapDelete("/job/{functionName}/{elementId}", DeleteJob)
            .WithName("DeleteJob")
            .Produces(200)
            .Produces(404)
            .AddEndpointFilter<HostPortEndpointFilter>();

        // Bloquer PUT et PATCH
        app.MapMethods("/job/{functionName}", new[] { "PUT", "PATCH" },
            () => Results.StatusCode((int)HttpStatusCode.MethodNotAllowed))
            .AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static async Task<IResult> CreateJob(
        string functionName,
        HttpContext context,
        [FromServices] IJobService jobService,
        [FromServices] IReplicasService replicasService,
        [FromServices] ILogger<Job> logger)
    {
        functionName = functionName.ToLowerInvariant();

        if (!IsValidFunctionName(functionName, logger))
        {
            return Results.BadRequest("Function name must match pattern [a-z-] and be between 3 and 12 c∫haracters");
        }

        CreateJob? createJob = await context.Request.ReadFromJsonAsync(
            CreateJobSerializerContext.Default.CreateJob);

        if (createJob == null)
        {
            return Results.BadRequest();
        }

        logger.LogInformation("Create job {JobName} with {CreateJob}", functionName, createJob);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("Create job details {CreateJob} ",
                JsonSerializer.Serialize(createJob, CreateJobSerializerContext.Default.CreateJob));
        }

        bool isMessageComeFromNamespaceInternal =
            FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService);

        var result = await jobService.EnqueueJobAsync(functionName, createJob, isMessageComeFromNamespaceInternal);

        if (!result.IsSuccess)
        {
            logger.LogWarning("Job HTTP Status {HttpStatusCode} with error {ErrorKey}",
                400, result.Error?.Key);
            return Results.BadRequest();
        }

        return Results.Json(
            new EnqueueJobResult(result.Data?.Id ?? ""),
            EnqueueJobResultSerializerContext.Default.EnqueueJobResult,
            statusCode: (int)HttpStatusCode.Accepted);
    }

    private static async Task<IResult> ListJobs(
        string functionName,
        [FromServices] IJobService jobService)
    {
        var jobs = await jobService.ListJobAsync(functionName);
        return Results.Json(jobs, JobListResultSerializerContext.Default.ListJobListResult);
    }

    private static async Task<IResult> DeleteJob(
        string functionName,
        string elementId,
        HttpContext context,
        [FromServices] IJobService jobService,
        [FromServices] IReplicasService replicasService,
        [FromServices] ILogger<Job> logger)
    {
        functionName = functionName.ToLowerInvariant();

        if (!IsValidFunctionName(functionName, logger))
        {
            return Results.BadRequest("Function name must match pattern [a-z-] and be between 3 and 12 characters");
        }

        bool isMessageComeFromNamespaceInternal =
            FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService);

        logger.LogInformation("Delete job {JobName} with {Id}", functionName, elementId);

        bool isSuccess = await jobService.DeleteJobAsync(functionName, elementId, isMessageComeFromNamespaceInternal);

        return isSuccess ? Results.Ok() : Results.NotFound();
    }
}

