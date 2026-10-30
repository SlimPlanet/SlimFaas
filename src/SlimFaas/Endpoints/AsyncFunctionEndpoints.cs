using MemoryPack;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Options;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.Endpoints;

public class AsyncFunction
{
}

public static class AsyncFunctionEndpoints
{
    private static readonly string[] s_allHttpMethods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];
    private const long AsyncBodyOffloadThresholdBytes = 1 * 1024L * 1024L;

    public static void MapAsyncFunctionEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /async-function/{functionName}/**
        app.MapMethods("/async-function/{functionName}/{**functionPath}",
            s_allHttpMethods,
            HandleAsyncFunction)
            .WithName("HandleAsyncFunction")
            .Produces(202)
            .Produces(404)
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();

        app.MapMethods("/async-function/{functionName}",
            s_allHttpMethods,
            (string functionName, HttpContext context,
                ILogger<AsyncFunction> logger,
                IReplicasService replicasService,
                IJobService jobService,
                ISlimFaasQueue slimFaasQueue,
                IFunctionAccessPolicy accessPolicy,
                IWebSocketFunctionRepository webSocketFunctionRepository,
                NetworkActivityTracker activityTracker,
                IClusterFileSync fileSync,
                IDatabaseService db,
                IOptions<ClusterFileOptions> fileOptions) =>
                HandleAsyncFunction(functionName, "", context, logger, replicasService, jobService, slimFaasQueue, accessPolicy, webSocketFunctionRepository, activityTracker, fileSync, db, fileOptions))
            .WithName("HandleAsyncFunctionRoot")
            .Produces(202)
            .Produces(404)
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();

        // POST /async-function-callback/{functionName}/{elementId}/{status}
        app.MapPost("/async-function-callback/{functionName}/{elementId}/{status}", HandleAsyncCallback)
            .WithName("HandleAsyncCallback")
            .Produces(200)
            .Produces(400)
            .Produces(404)
            .AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static async Task<IResult> HandleAsyncFunction(
        string functionName,
        string? functionPath,
        HttpContext context,
        [FromServices] ILogger<AsyncFunction> logger,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService,
        [FromServices] ISlimFaasQueue slimFaasQueue,
        [FromServices] IFunctionAccessPolicy accessPolicy,
        [FromServices] IWebSocketFunctionRepository webSocketFunctionRepository,
        [FromServices] NetworkActivityTracker activityTracker,
        [FromServices] IClusterFileSync fileSync,
        [FromServices] IDatabaseService db,
        [FromServices] IOptions<ClusterFileOptions> fileOptions)
    {
        functionPath ??= "";

        // Chercher d'abord dans les fonctions Kubernetes classiques
        DeploymentInformation? function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);

        // Puis dans les fonctions WebSocket virtuelles
        if (function == null)
        {
            function = webSocketFunctionRepository.GetVirtualDeployments()
                .FirstOrDefault(d => string.Equals(d.Deployment, functionName, StringComparison.OrdinalIgnoreCase));
        }

        if (function == null)
        {
            return Results.NotFound();
        }

        if (!accessPolicy.CanAccessFunction(context, function, functionPath))
        {
            return Results.NotFound();
        }

        var defaultAsync = function.Configuration.DefaultAsync;
        var deadLetters = context.RequestServices.GetService<SlimFaas.DeadLetters.DeadLetterService>();
        if (deadLetters is not null)
            await deadLetters.ConfigureAsync(function);

        string queueElementId = Guid.NewGuid().ToString();
        var activityCaller = FunctionEndpointsHelpers.ResolveNetworkActivityCaller(
            context,
            jobService,
            FunctionEndpointsHelpers.HasLocalWorkloadIdentity(context)
                ? FunctionEndpointsHelpers.GetLocalWorkloadToken(context)
                : string.Empty, replicasService);

        CustomRequest customRequest = await FunctionEndpointsHelpers.InitCustomRequest(
            context,
            context.Request,
            functionName,
            functionPath ?? "",
            bodyOffloadThresholdBytes: AsyncBodyOffloadThresholdBytes,
            queueElementId: queueElementId,
            fileSync: fileSync,
            db: db,
            unknownLengthReservationBytes: fileOptions.Value.UnknownLengthReservationBytes,
            ct: context.RequestAborted);

        var bin = MemoryPackSerializer.Serialize(customRequest);
        string requestInId = activityTracker.Record(
            NetworkActivityTracker.EventTypes.RequestIn,
            activityCaller.Actor,
            NetworkActivityTracker.Actors.SlimFaas,
            sourcePod: activityCaller.SourcePod);

        string id;
        try
        {
            id = await slimFaasQueue.EnqueueAsync(
                functionName,
                bin,
                new RetryInformation(
                    defaultAsync.TimeoutRetries,
                    defaultAsync.HttpTimeout,
                    defaultAsync.HttpStatusRetries), queueElementId);
        }
        catch
        {
            activityTracker.Record(
                NetworkActivityTracker.EventTypes.RequestEnd,
                activityCaller.Actor,
                NetworkActivityTracker.Actors.SlimFaas,
                sourcePod: activityCaller.SourcePod,
                correlationId: requestInId);
            throw;
        }

        activityTracker.Record(
            NetworkActivityTracker.EventTypes.Enqueue,
            NetworkActivityTracker.Actors.SlimFaas,
            functionName,
            functionName,
            sourcePod: activityCaller.SourcePod,
            correlationId: requestInId);

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
        if (elementId.StartsWith("stream_", StringComparison.Ordinal))
        {
            var streams = context.RequestServices.GetRequiredService<SlimFaas.Streams.IStreamService>();
            var payload = await streams.ReadAsync(context.RequestAborted);
            var attempt = DurableStreamStore.Attempt(payload, elementId);
            var definition = attempt is null ? null : DurableStreamStore.Definition(payload, attempt.Stream);
            if (attempt is null || definition is null || attempt.Function != functionName ||
                !streams.CanAccess(context, definition) || !streams.CanObserveFunction(context, functionName))
                return Results.NotFound();
            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest("Status must be success or error.");
            var result = await streams.MutateAsync(new()
            {
                Action = DurableStreamAction.Complete, Stream = attempt.Stream, Function = functionName, ElementId = elementId,
                StatusCode = string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ? 200 : 500
            }, context.RequestAborted);
            return Results.StatusCode(result.Status);
        }
        DeploymentInformation? function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
        function ??= context.RequestServices.GetService<IWebSocketFunctionRepository>()?.GetVirtualDeployments()
            .FirstOrDefault(item => item.Deployment == functionName);
        if (function == null)
        {
            return Results.NotFound();
        }

        var visibility = FunctionEndpointsHelpers.GetFunctionVisibility(logger, function, "");
        if (visibility == FunctionVisibility.Private &&
            !FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService))
        {
            return Results.NotFound();
        }

        if (string.IsNullOrEmpty(elementId) || string.IsNullOrEmpty(status))
        {
            return Results.BadRequest();
        }

        int statusCode = string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ? 200 : 500;
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest("Status must be success or error.");

        var deadLetters = context.RequestServices.GetService<SlimFaas.DeadLetters.DeadLetterService>();
        if (deadLetters is not null)
        {
            await deadLetters.ConfigureAsync(function);
            if (deadLetters.IsManaged(function.Deployment))
            {
                await deadLetters.MutateAsync(new()
                {
                    Action = DeadLetterAction.Complete, Function = function.Deployment,
                    ElementId = elementId, HttpStatus = statusCode, Source = "Callback"
                });
                return Results.Ok();
            }
        }

        var items = new ListQueueItemStatus
        {
            Items =
            [
                new QueueItemStatus(elementId, statusCode)
            ]
        };

        await slimFaasQueue.ListCallbackAsync(function.Deployment, items);
        return Results.Ok();
    }
}
