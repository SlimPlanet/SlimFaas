using Microsoft.AspNetCore.Mvc;
using SlimFaas.Kubernetes;

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
    }

    private static IResult GetAllFunctionStatuses(
        [FromServices] IReplicasService replicasService)
    {
        IList<SlimFaas.FunctionStatus> functionStatuses = replicasService.Deployments.Functions
            .Select(FunctionEndpointsHelpers.MapToFunctionStatus)
            .ToList();

        return Results.Json(functionStatuses,
            SlimFaas.ListFunctionStatusSerializerContext.Default.ListFunctionStatus);
    }

    private static IResult GetFunctionStatus(
        string functionName,
        [FromServices] IReplicasService replicasService)
    {
        DeploymentInformation? functionDeploymentInformation =
            FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);

        if (functionDeploymentInformation == null)
        {
            return Results.NotFound();
        }

        SlimFaas.FunctionStatus functionStatus = FunctionEndpointsHelpers.MapToFunctionStatus(functionDeploymentInformation);
        return Results.Json(functionStatus, SlimFaas.FunctionStatusSerializerContext.Default.FunctionStatus);
    }

    private static IResult WakeFunction(
        string functionName,
        [FromServices] IReplicasService replicasService,
        [FromServices] IWakeUpFunction wakeUpFunction)
    {
        DeploymentInformation? function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);

        if (function == null)
        {
            return Results.NotFound();
        }

#pragma warning disable CS4014
        wakeUpFunction.FireAndForgetWakeUpAsync(functionName);
#pragma warning restore CS4014

        return Results.NoContent();
    }
}

