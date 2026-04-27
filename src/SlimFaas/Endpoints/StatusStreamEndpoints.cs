using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
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
        [FromServices] NetworkActivityTracker tracker,
        [FromServices] IReplicasService replicasService,
        [FromServices] IJobService jobService,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("StatusStreamEndpoints");
        if (!FunctionEndpointsHelpers.MessageComeFromNamespaceInternal(logger, context, replicasService, jobService))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

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
        [FromServices] NetworkActivityTracker tracker,
        [FromServices] IStatusStreamSnapshotCache snapshotCache,
        [FromServices] IOptions<SlimFaasOptions> slimFaasOptions,
        [FromServices] ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("StatusStreamEndpoints");
        var ct = context.RequestAborted;
        if (!tracker.TrySubscribe(out var reader, out var channel))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.Response.WriteAsync("Too many status stream clients.", ct);
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";

        try
        {
            // Send initial full state
            await context.Response.WriteAsync(await snapshotCache.GetStateFrameAsync(includeRecentActivity: true, ct), ct);
            await context.Response.Body.FlushAsync(ct);

            // Then send periodic full state + activity events.
            // Activity is event-driven to avoid waiting for the next state tick under bursts.
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(
                slimFaasOptions.Value.StatusStream.StateIntervalMilliseconds));
            int activityBatchSize = Math.Max(1, slimFaasOptions.Value.StatusStream.LiveActivityBatchSize);
            var activityAvailableTask = reader.WaitToReadAsync(ct).AsTask();
            var stateTickTask = timer.WaitForNextTickAsync(ct).AsTask();

            while (!ct.IsCancellationRequested)
            {
                var completedTask = await Task.WhenAny(activityAvailableTask, stateTickTask);

                if (completedTask == activityAvailableTask)
                {
                    if (!await activityAvailableTask)
                    {
                        break;
                    }

                    await WritePendingActivityAsync(context, reader, activityBatchSize, ct);

                    if (stateTickTask.IsCompleted)
                    {
                        if (!await stateTickTask)
                        {
                            break;
                        }

                        await context.Response.WriteAsync(await snapshotCache.GetStateFrameAsync(includeRecentActivity: false, ct), ct);
                        stateTickTask = timer.WaitForNextTickAsync(ct).AsTask();
                    }

                    await context.Response.Body.FlushAsync(ct);
                    activityAvailableTask = reader.WaitToReadAsync(ct).AsTask();
                    continue;
                }

                if (!await stateTickTask)
                {
                    break;
                }

                // Send full state periodically
                await context.Response.WriteAsync(await snapshotCache.GetStateFrameAsync(includeRecentActivity: false, ct), ct);
                await context.Response.Body.FlushAsync(ct);
                stateTickTask = timer.WaitForNextTickAsync(ct).AsTask();
            }
        }
        catch (OperationCanceledException ex)
        {
            logger.LogDebug(ex, "Status stream client disconnected.");
        }
        finally
        {
            tracker.Unsubscribe(channel);
        }
    }

    private static async Task WritePendingActivityAsync(
        HttpContext context,
        ChannelReader<NetworkActivityEvent> reader,
        int activityBatchSize,
        CancellationToken ct)
    {
        int maxEventsPerWake = activityBatchSize * 10;
        int writtenEvents = 0;

        while (writtenEvents < maxEventsPerWake && reader.TryRead(out var firstEvent))
        {
            var batch = new List<NetworkActivityEvent>(activityBatchSize) { firstEvent };
            while (batch.Count < activityBatchSize
                   && writtenEvents + batch.Count < maxEventsPerWake
                   && reader.TryRead(out var nextEvent))
            {
                batch.Add(nextEvent);
            }

            writtenEvents += batch.Count;

            if (batch.Count == 1)
            {
                string json = JsonSerializer.Serialize(batch[0], StatusStreamSerializerContext.Default.NetworkActivityEvent);
                await context.Response.WriteAsync($"event: activity\ndata: {json}\n\n", ct);
                continue;
            }

            string batchJson = JsonSerializer.Serialize(batch, StatusStreamSerializerContext.Default.ListNetworkActivityEvent);
            await context.Response.WriteAsync($"event: activity_batch\ndata: {batchJson}\n\n", ct);
        }
    }
}


