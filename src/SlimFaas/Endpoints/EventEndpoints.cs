using Microsoft.AspNetCore.Mvc;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Security;

namespace SlimFaas.Endpoints;

public static class EventEndpoints
{
    public static void MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /publish-event/{eventName}/**
        app.MapPost("/publish-event/{eventName}/{**functionPath}", PublishEvent)
            .WithName("PublishEvent")
            .Produces(204)
            .Produces(404)
            .DisableAntiforgery();

        app.MapPost("/publish-event/{eventName}",
            (string eventName, HttpContext context,
                ILogger<SlimProxyMiddleware> logger,
                HistoryHttpMemoryService historyHttpService,
                ISendClient sendClient,
                IReplicasService replicasService,
                IJobService jobService,
                IFunctionAccessPolicy accessPolicy) =>
                PublishEvent(eventName, "", context, logger, historyHttpService, sendClient, replicasService, jobService, accessPolicy))
            .WithName("PublishEventRoot")
            .Produces(204)
            .Produces(404)
            .DisableAntiforgery();
    }

    private static async Task<IResult> PublishEvent(
        string eventName,
        string? functionPath,
        HttpContext context,
        [FromServices] ILogger<SlimProxyMiddleware> logger,
        [FromServices] HistoryHttpMemoryService historyHttpService,
        [FromServices] ISendClient sendClient,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService,
        [FromServices] IFunctionAccessPolicy accessPolicy)
    {
        functionPath ??= "";

        logger.LogDebug("Receiving event: {EventName}", eventName);
        var functions = accessPolicy.GetAllowedSubscribers(context, eventName);

        if (functions.Count <= 0)
        {
            logger.LogDebug("Publish-event {EventName} : Return 404 from event", eventName);
            return Results.NotFound();
        }

        var lastSetTicks = DateTime.UtcNow.Ticks;
        List<DeploymentInformation> calledFunctions = new();
        CustomRequest customRequest = await FunctionEndpointsHelpers.InitCustomRequest(
            context, context.Request, "", functionPath);

        List<Task> tasks = new();
        var queryString = context.Request.QueryString.ToUriComponent();

        foreach (DeploymentInformation function in functions)
        {
            logger.LogDebug("Publish-event list {EventName} : Deployment {Deployment}", eventName, function.Deployment);

            foreach (var pod in function.Pods)
            {
                logger.LogDebug("Publish-event pod {Ready} endpoint {EndpointReady} IP: {Deployment}",
                    pod.Ready, function.EndpointReady, pod.Ip);

                if (pod.Ready is not true || !function.EndpointReady)
                {
                    continue;
                }

                if (!calledFunctions.Contains(function))
                {
                    calledFunctions.Add(function);
                }

                logger.LogInformation("Publish-event {EventName} : Deployment {Deployment} Pod {PodName} is ready: {PodReady}",
                    eventName, function.Deployment, pod.Name, pod.Ready);

                historyHttpService.SetTickLastCall(function.Deployment, lastSetTicks);

                string baseFunctionPodUrl = Environment.GetEnvironmentVariable(EnvironmentVariables.BaseFunctionPodUrl) ??
                                          EnvironmentVariables.BaseFunctionPodUrlDefault;

                var baseUrl = SlimDataEndpoint.Get(pod, baseFunctionPodUrl);
                logger.LogDebug("Sending event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent}",
                    eventName, function.Deployment, baseUrl, functionPath, context.Request.QueryString.ToUriComponent());

                Task task = SendRequestAsync(queryString, sendClient, customRequest with { FunctionName = function.Deployment },
                    baseUrl, logger, eventName, function.Configuration.DefaultPublish);
                tasks.Add(task);
            }
        }

        while (tasks.Any(t => !t.IsCompleted) && !context.RequestAborted.IsCancellationRequested)
        {
            await Task.Delay(20, context.RequestAborted);
            bool isOneSecondElapsed = new DateTime(lastSetTicks, DateTimeKind.Utc) < DateTime.UtcNow.AddSeconds(-1);

            if (!isOneSecondElapsed)
            {
                continue;
            }

            lastSetTicks = DateTime.UtcNow.Ticks;
            foreach (DeploymentInformation function in calledFunctions)
            {
                historyHttpService.SetTickLastCall(function.Deployment, lastSetTicks);
            }
        }

        return Results.NoContent();
    }

    private static async Task SendRequestAsync(
        string queryString,
        ISendClient sendClient,
        CustomRequest customRequest,
        string baseUrl,
        ILogger logger,
        string eventName,
        SlimFaasDefaultConfiguration slimFaasDefaultConfiguration)
    {
        try
        {
            using HttpResponseMessage responseMessage = await sendClient.SendHttpRequestAsync(
                customRequest, slimFaasDefaultConfiguration, baseUrl);

            logger.LogDebug(
                "Response from event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent} is {StatusCode}",
                eventName, customRequest.FunctionName, baseUrl, customRequest.Path, queryString,
                responseMessage.StatusCode);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Error in sending event {EventName} to {FunctionDeployment} at {BaseUrl} with path {FunctionPath} and query {UriComponent}",
                eventName, customRequest.FunctionName, baseUrl, customRequest.Path, queryString);
        }
    }
}

