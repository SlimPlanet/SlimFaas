using MemoryPack;
using Microsoft.AspNetCore.Mvc;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;

namespace SlimFaas.Endpoints;

public class AsyncFunction
{
}

public static class AsyncFunctionEndpoints
{
    public static void MapAsyncFunctionEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /async-function/{functionName}/**
        app.MapMethods("/async-function/{functionName}/{**functionPath}",
            new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" },
            HandleAsyncFunction)
            .WithName("HandleAsyncFunction")
            .Produces(202)
            .Produces(404)
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();

        app.MapMethods("/async-function/{functionName}",
            new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" },
            (string functionName, HttpContext context,
                ILogger<AsyncFunction> logger,
                IReplicasService replicasService,
                IJobService jobService,
                ISlimFaasQueue slimFaasQueue,
                IFunctionAccessPolicy accessPolicy) =>
                HandleAsyncFunction(functionName, "", context, logger, replicasService, jobService, slimFaasQueue, accessPolicy))
            .WithName("HandleAsyncFunctionRoot")
            .Produces(202)
            .Produces(404)
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();

        // POST /async-function-callback/{functionName}/{elementId}/{status}
        app.MapPost("/async-function-callback/{functionName}/{elementId}/{status}", HandleAsyncCallback)
            .WithName("HandleAsyncCallback")
            .Produces(200)
            .Produces(400)
            .Produces(404)
            .AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter<OpenTelemetryEnrichmentFilter>();
    }

    private static async Task<IResult> HandleAsyncFunction(
        string functionName,
        string? functionPath,
        HttpContext context,
        [FromServices] ILogger<AsyncFunction> logger,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService,
        [FromServices] ISlimFaasQueue slimFaasQueue,
        [FromServices] IFunctionAccessPolicy accessPolicy)
    {
        functionPath ??= "";

        DeploymentInformation? function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
        if (function == null)
        {
            return Results.NotFound();
        }

        if (!accessPolicy.CanAccessFunction(context, function, functionPath))
        {
            return Results.NotFound();
        }

        CustomRequest customRequest = await FunctionEndpointsHelpers.InitCustomRequest(
            context, context.Request, functionName, functionPath);

        var bin = MemoryPackSerializer.Serialize(customRequest);
        var defaultAsync = function.Configuration.DefaultAsync;
        var id = await slimFaasQueue.EnqueueAsync(
            functionName,
            bin,
            new RetryInformation(
                defaultAsync.TimeoutRetries,
                defaultAsync.HttpTimeout,
                defaultAsync.HttpStatusRetries));

        context.Response.Headers.Append(SlimQueuesWorker.SlimfaasElementId, id);
        return Results.Accepted();
    }

    private static async Task<IResult> HandleAsyncCallback(
        string functionName,
        string elementId,
        string status,
        HttpContext context,
        [FromServices] ILogger<AsyncFunction> logger,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService,
        [FromServices] ISlimFaasQueue slimFaasQueue)
    {
        DeploymentInformation? function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
        if (function == null)
        {
            return Results.NotFound();
        }

        var visibility = FunctionEndpointsHelpers.GetFunctionVisibility(logger, function, "");
        if (visibility == FunctionVisibility.Private &&
            !FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService, function))
        {
            return Results.NotFound();
        }

        if (string.IsNullOrEmpty(elementId) || string.IsNullOrEmpty(status))
        {
            return Results.BadRequest();
        }

        var items = new ListQueueItemStatus
        {
            Items =
            [
                new QueueItemStatus(elementId, status.ToLowerInvariant() == "success" ? 200 : 500)
            ]
        };

        await slimFaasQueue.ListCallbackAsync(function.Deployment, items);
        return Results.Ok();
    }
}

