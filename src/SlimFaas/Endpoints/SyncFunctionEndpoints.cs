using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;

namespace SlimFaas.Endpoints;

public class SyncFunction
{
}

public static class SyncFunctionEndpoints
{
    public static void MapSyncFunctionEndpoints(this IEndpointRouteBuilder app)
    {
        // Toutes les routes /function/{functionName}/**
        app.MapMethods("/function/{functionName}/{**functionPath}",
            new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" },
            HandleSyncFunction)
            .WithName("HandleSyncFunction")
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();

        app.MapMethods("/function/{functionName}",
            new[] { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" },
            (string functionName, HttpContext context,
                ILogger<SyncFunction> logger,
                HistoryHttpMemoryService historyHttpService,
                ISendClient sendClient,
                IReplicasService replicasService,
                IJobService jobService) =>
                HandleSyncFunction(functionName, "", context, logger, historyHttpService, sendClient, replicasService, jobService))
            .WithName("HandleSyncFunctionRoot")
            .DisableAntiforgery()
            .AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static async Task<IResult> HandleSyncFunction(
        string functionName,
        string? functionPath,
        HttpContext context,
        [FromServices] ILogger<SyncFunction> logger,
        [FromServices] HistoryHttpMemoryService historyHttpService,
        [FromServices] ISendClient sendClient,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService)
    {
        functionPath ??= "";
        var ct = context.RequestAborted;

        var function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
        if (function is null)
        {
            logger.LogDebug("{FunctionName} not found 404", functionName);
            return Results.NotFound();
        }

        var visibility = FunctionEndpointsHelpers.GetFunctionVisibility(logger, function, functionPath);
        if (visibility == FunctionVisibility.Private &&
            !FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService, function))
        {
            logger.LogDebug("{FunctionName} not found 404 because is private 404", functionName);
            return Results.NotFound();
        }

        await WaitForAnyPodStartedAsync(logger, context, historyHttpService, replicasService, functionName);

        Task<HttpResponseMessage> responseTask = sendClient.SendHttpRequestSync(
            context,
            functionName,
            functionPath,
            context.Request.QueryString.ToUriComponent(),
            function.Configuration.DefaultSync,
            null,
            new Proxy(replicasService, functionName));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);

            while (true)
            {
                var nextTickTask = timer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(responseTask, nextTickTask);

                if (completed == responseTask)
                    break;

                historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogDebug("Request aborted by client for {FunctionName}", functionName);
            return Results.StatusCode(499); // Client Closed Request
        }

        using var responseMessage = await responseTask.ConfigureAwait(false);

        context.Response.StatusCode = (int)responseMessage.StatusCode;
        CopyFromTargetResponseHeaders(context, responseMessage);

        var stream = await responseMessage.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await stream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);

        historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);

        return Results.Empty;
    }

    private static bool IsFunctionReady(DeploymentInformation f) =>
        (f?.Pods?.Any(p => p?.Ready == true) ?? false) && f?.EndpointReady == true;

    private static async Task WaitForAnyPodStartedAsync(
        ILogger logger,
        HttpContext context,
        HistoryHttpMemoryService historyHttpService,
        IReplicasService replicasService,
        string functionName)
    {
        var function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
        if (function is null) return;

        var timeout = TimeSpan.FromSeconds(function.Configuration.DefaultSync.HttpTimeout);
        var sw = Stopwatch.StartNew();

        historyHttpService.SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
        var lastTickUpdate = sw.Elapsed;

        var basePoll = TimeSpan.FromMilliseconds(100);

        try
        {
            while (true)
            {
                function = FunctionEndpointsHelpers.SearchFunction(replicasService, functionName);
                if (function is null) return;

                if (IsFunctionReady(function))
                {
                    logger.LogDebug("WaitForAnyPodStartedAsync: {FunctionName} is ready (EndpointReady={EndpointReady}).",
                        functionName, function.EndpointReady);
                    return;
                }

                foreach (var pod in function.Pods ?? Enumerable.Empty<PodInformation>())
                {
                    logger.LogDebug("Pod {PodName} Ready={Ready} IP={Ip}", pod.Name, pod.Ready, pod.Ip);
                }

                if (sw.Elapsed - lastTickUpdate >= TimeSpan.FromSeconds(1))
                {
                    var nowTicks = DateTime.UtcNow.Ticks;
                    historyHttpService.SetTickLastCall(functionName, nowTicks);
                    lastTickUpdate = sw.Elapsed;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    logger.LogWarning("WaitForAnyPodStartedAsync: timeout ({Timeout}s) atteint pour {FunctionName}.",
                        timeout.TotalSeconds, functionName);
                    return;
                }

                if (context.RequestAborted.IsCancellationRequested) return;

                var delay = remaining <= basePoll ? remaining : basePoll;
                await Task.Delay(delay, context.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("WaitForAnyPodStartedAsync: annulé pour {FunctionName}.", functionName);
        }
    }

    private static void CopyFromTargetResponseHeaders(HttpContext context, HttpResponseMessage responseMessage)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in responseMessage.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (KeyValuePair<string, IEnumerable<string>> header in responseMessage.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");
    }
}

