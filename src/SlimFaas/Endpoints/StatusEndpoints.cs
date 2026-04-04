using Microsoft.AspNetCore.Mvc;

namespace SlimFaas.Endpoints;

public static class StatusEndpoints
{
    public static void MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /status-functions - Liste tous les statuts
        app.MapGet("/status-functions", GetAllFunctionStatuses)
            .WithName("GetAllFunctionStatuses")
            .Produces<List<SlimFaas.FunctionStatus>>(200)
            .AddEndpointFilter<HostPortEndpointFilter>();

        // GET /status-function/{functionName} - Statut d'une fonction
        app.MapGet("/status-function/{functionName}", GetFunctionStatus)
            .WithName("GetFunctionStatus")
            .Produces<SlimFaas.FunctionStatus>(200)
            .Produces(404)
            .AddEndpointFilter<HostPortEndpointFilter>();

        // POST /wake-function/{functionName} - Réveiller une fonction
        app.MapPost("/wake-function/{functionName}", WakeFunction)
            .WithName("WakeFunction")
            .Produces(204)
            .Produces(404)
            .AddEndpointFilter<HostPortEndpointFilter>();

        // POST /wake-functions - Réveiller toutes les fonctions en un seul appel
        app.MapPost("/wake-functions", WakeAllFunctions)
            .WithName("WakeAllFunctions")
            .Produces(204)
            .AddEndpointFilter<HostPortEndpointFilter>();
    }


    private static IResult GetAllFunctionStatuses(
        HttpContext context,
        [FromServices] IReplicasService replicasService,
        [FromServices] FunctionStatusCache cache)
    {

        var statuses = cache.GetAll(replicasService);

        return Results.Json(statuses,
            SlimFaas.ListFunctionStatusSerializerContext.Default.ListFunctionStatus);
    }

    private static IResult GetFunctionStatus(
        string functionName,
        [FromServices] IReplicasService replicasService,
        [FromServices] FunctionStatusCache cache)
    {
        var status = cache.GetOne(replicasService, functionName);
        if (status is null) return Results.NotFound();

        return Results.Json(status,
            SlimFaas.FunctionStatusSerializerContext.Default.FunctionStatus);
    }

    private static IResult WakeAllFunctions(
        [FromServices] IReplicasService replicasService,
        [FromServices] WakeUpGate gate,
        [FromServices] IWakeUpFunction wakeUpFunction,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SlimFaas.WakeAll");
        var functions = replicasService.Deployments?.Functions ?? [];

        foreach (var function in functions)
        {
            var name = function.Deployment;
            if (!gate.TryEnter(name)) continue; // déjà en cours

#pragma warning disable CS4014
            var t = wakeUpFunction.FireAndForgetWakeUpAsync(name);
            _ = t.ContinueWith(task =>
            {
                gate.Exit(name);
                if (task.IsFaulted && task.Exception is not null)
                    logger.LogError(task.Exception, "WakeAll failed for {FunctionName}", name);
            }, TaskScheduler.Default);
#pragma warning restore CS4014
        }

        return Results.NoContent();
    }

    private static IResult WakeFunction(
        string functionName,
        [FromServices] IReplicasService replicasService,
        [FromServices] WakeUpGate gate,
        [FromServices] IWakeUpFunction wakeUpFunction,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("SlimFaas.Wake");

        var function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
        if (function is null) return Results.NotFound();

        // Coalescing anti-spam
        if (!gate.TryEnter(functionName))
            return Results.NoContent(); // déjà réveillée / en cours

#pragma warning disable CS4014
        var t = wakeUpFunction.FireAndForgetWakeUpAsync(functionName);

        _ = t.ContinueWith(task =>
        {
            gate.Exit(functionName);
            if (task.IsFaulted && task.Exception is not null)
                logger.LogError(task.Exception, "Wake failed for {FunctionName}", functionName);
        }, TaskScheduler.Default);
#pragma warning restore CS4014

        return Results.NoContent();
    }
}

