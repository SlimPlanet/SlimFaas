using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SlimFaas.Database;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Endpoints;

public static class StatusStreamEndpoints
{
    public static void MapStatusStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/status-functions-stream", HandleStream)
            .WithName("StatusFunctionsStream")
            .AddEndpointFilter<HostPortEndpointFilter>();

        // Internal endpoint used by peer SlimFaas nodes to scrape local activity events.
        // Query param: since (unix ms timestamp) — only returns events after that timestamp.
        app.MapGet("/internal/activity-events", HandleActivityEvents)
            .WithName("InternalActivityEvents")
            .AddEndpointFilter<HostPortEndpointFilter>();
    }

    /// <summary>
    /// Returns the local activity events since a given timestamp.
    /// Used by peer nodes to aggregate a global view.
    /// </summary>
    private static IResult HandleActivityEvents(
        HttpContext context,
        [FromServices] NetworkActivityTracker tracker)
    {
        long since = 0;
        if (context.Request.Query.TryGetValue("since", out var sinceVal)
            && long.TryParse(sinceVal.FirstOrDefault(), out var parsed))
        {
            since = parsed;
        }

        var events = tracker.GetLocalSince(since);
        return Results.Json(events, StatusStreamSerializerContext.Default.ListNetworkActivityEvent);
    }

    private static async Task HandleStream(
        HttpContext context,
        [FromServices] IReplicasService replicasService,
        [FromServices] FunctionStatusCache cache,
        [FromServices] NetworkActivityTracker tracker,
        [FromServices] ISlimFaasQueue slimFaasQueue,
        [FromServices] IOptions<SlimFaasOptions> slimFaasOptions,
        [FromServices] INamespaceProvider? namespaceProvider,
        [FromServices] IJobConfiguration? jobConfiguration,
        [FromServices] IJobService? jobService,
        [FromServices] IScheduleJobService? scheduleJobService)
    {
        // ...existing code...
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        var ct = context.RequestAborted;
        var (reader, channel) = tracker.Subscribe();

        try
        {
            // Send initial full state
            var currentNamespace = namespaceProvider?.CurrentNamespace ?? slimFaasOptions.Value.Namespace ?? "default";
            await SendFullState(context, replicasService, cache, tracker, slimFaasQueue, slimFaasOptions.Value.EnableFront, currentNamespace, jobConfiguration, jobService, scheduleJobService, ct);

            // Then send periodic full state + activity events
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

            while (!ct.IsCancellationRequested)
            {
                // Drain any activity events that arrived
                while (reader.TryRead(out var evt))
                {
                    string json = JsonSerializer.Serialize(evt, StatusStreamSerializerContext.Default.NetworkActivityEvent);
                    await context.Response.WriteAsync($"event: activity\ndata: {json}\n\n", ct);
                }

                await context.Response.Body.FlushAsync(ct);

                // Wait for next tick
                if (!await timer.WaitForNextTickAsync(ct))
                    break;

                // Send full state periodically
                var currentNamespaceTick = namespaceProvider?.CurrentNamespace ?? slimFaasOptions.Value.Namespace ?? "default";
                await SendFullState(context, replicasService, cache, tracker, slimFaasQueue, slimFaasOptions.Value.EnableFront, currentNamespaceTick, jobConfiguration, jobService, scheduleJobService, ct);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        finally
        {
            tracker.Unsubscribe(channel);
        }
    }

    private static async Task SendFullState(
        HttpContext context,
        IReplicasService replicasService,
        FunctionStatusCache cache,
        NetworkActivityTracker tracker,
        ISlimFaasQueue slimFaasQueue,
        bool frontEnabled,
        string kubeNamespace,
        IJobConfiguration? jobConfiguration,
        IJobService? jobService,
        IScheduleJobService? scheduleJobService,
        CancellationToken ct)
    {
        try
        {
            // Keep the stream state fresh for add/remove operations.
            await replicasService.SyncDeploymentsAsync(kubeNamespace);
        }
        catch
        {
            // Keep streaming with the latest known snapshot if a sync attempt fails.
        }

        var functions = cache.GetAllDetailed(replicasService);
        var jobs = new List<JobConfigurationStatus>();
        if (jobConfiguration != null && jobService != null)
        {
            await jobConfiguration.SyncJobsConfigurationAsync();
            await jobService.SyncJobsAsync();
            jobs = await JobStatusEndpoints.BuildJobStatusesAsync(jobConfiguration, jobService, scheduleJobService);
        }

        // Gather queue lengths
        var queues = new List<QueueInfo>();
        foreach (var fn in functions)
        {
            try
            {
                long length = await slimFaasQueue.CountElementAsync(
                    fn.Name,
                    new List<CountType> { CountType.Available, CountType.Running, CountType.WaitingForRetry },
                    int.MaxValue);
                queues.Add(new QueueInfo(fn.Name, length));
            }
            catch
            {
                queues.Add(new QueueInfo(fn.Name, 0));
            }
        }

        var slimFaasInfo = replicasService.Deployments.SlimFaas;
        var slimFaasNodes = slimFaasInfo.Pods
            .Select(p => new SlimFaasNodeInfo(
                p.Name,
                p.Ready == true ? "Running" : (p.Started == true ? "Starting" : "Pending")))
            .ToList();

        var payload = new StatusStreamPayload(
            Functions: functions,
            Queues: queues,
            Jobs: jobs,
            RecentActivity: tracker.GetRecent(),
            SlimFaasReplicas: slimFaasInfo.Replicas,
            SlimFaasNodes: slimFaasNodes,
            FrontEnabled: frontEnabled,
            FrontMessage: frontEnabled ? null : "SlimFaas front is disabled by configuration (SlimFaas:EnableFront=false).");

        string json = JsonSerializer.Serialize(payload, StatusStreamSerializerContext.Default.StatusStreamPayload);
        await context.Response.WriteAsync($"event: state\ndata: {json}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
}


