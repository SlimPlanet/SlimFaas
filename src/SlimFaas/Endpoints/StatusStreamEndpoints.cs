using System.Text.Json;
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

            // Then send periodic full state + activity events
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(
                slimFaasOptions.Value.StatusStream.StateIntervalMilliseconds));

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
                await context.Response.WriteAsync(await snapshotCache.GetStateFrameAsync(includeRecentActivity: false, ct), ct);
                await context.Response.Body.FlushAsync(ct);
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
}


