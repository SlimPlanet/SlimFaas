using System.Text.Json;
using System.Net;
using Microsoft.Extensions.Options;
using SlimFaas.Options;
using SlimFaas.Scaling;

namespace SlimFaas.Endpoints;

public static class ScalingEndpoints
{
    public static void MapScalingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/status-scaling-stream", Stream).AddEndpointFilter<HostPortEndpointFilter>();
        app.MapPost("/debug/scaling/simulate", Simulate).AddEndpointFilter<HostPortEndpointFilter>();
        app.MapGet("/internal/scaling/state", InternalState).AddEndpointFilter<HostPortEndpointFilter>();
        app.MapPost("/internal/scaling/simulate", InternalSimulate).AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static bool ValidFunction(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 253 && !name.Any(char.IsControl);

    private static async Task Stream(HttpContext context, IScalingLeaderClient leader, NetworkActivityTracker tracker,
        IOptions<SlimFaasOptions> options)
    {
        if (!options.Value.EnableFront) { context.Response.StatusCode = 404; return; }
        string function = context.Request.Query["function"].ToString();
        if (!ValidFunction(function)) { context.Response.StatusCode = 400; return; }
        if (!tracker.TryReserveStreamClient())
        { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "3"; return; }
        var ct = context.RequestAborted;
        try
        {
            string? session = null;
            long cursor = -1;
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.Value.StatusStream.StateIntervalMilliseconds));
            do
            {
                ScalingState state;
                try { state = await leader.GetStateAsync(function, ct); }
                catch (ScalingSimulationException e)
                {
                    if (!context.Response.HasStarted)
                    { await Error(context, e.StatusCode, e.Message); return; }
                    await context.Response.WriteAsync($"event: scaling_error\ndata: {JsonSerializer.Serialize(new ScalingError(e.Message), ScalingJsonContext.Default.ScalingError)}\n\n", ct);
                    await context.Response.Body.FlushAsync(ct);
                    return;
                }
                if (session != state.Session) { session = state.Session; cursor = -1; }
                var events = state.Events.Where(e => e.Id > cursor).ToArray();
                if (events.Length > 0) cursor = events[^1].Id;
                if (!context.Response.HasStarted)
                {
                    context.Response.ContentType = "text/event-stream";
                    context.Response.Headers.CacheControl = "no-cache, no-store";
                    context.Response.Headers["X-Accel-Buffering"] = "no";
                }
                string json = JsonSerializer.Serialize(state with { Events = events }, ScalingJsonContext.Default.ScalingState);
                await context.Response.WriteAsync($"event: scaling_state\ndata: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { }
        finally { tracker.ReleaseStreamClient(); }
    }

    private static async Task InternalState(HttpContext context, ScalingLeaderClient leader,
        IReplicasService replicas, IOptions<SlimFaasOptions> options)
    {
        if (!options.Value.EnableFront || !IsPeerRequest(context, replicas)) { context.Response.StatusCode = 404; return; }
        string function = context.Request.Query["function"].ToString();
        if (!ValidFunction(function)) { context.Response.StatusCode = 400; return; }
        try { await Write(context, leader.LocalState(function), ScalingJsonContext.Default.ScalingState); }
        catch (ScalingSimulationException e) { await Error(context, e.StatusCode, e.Message); }
    }

    private static Task Simulate(HttpContext context, IScalingLeaderClient leader, IOptions<SlimFaasOptions> options)
        => HandleSimulation(context, options.Value.EnableFront, leader.SimulateAsync);

    private static Task InternalSimulate(HttpContext context, ScalingLeaderClient leader,
        IReplicasService replicas, IOptions<SlimFaasOptions> options)
        => HandleSimulation(context, options.Value.EnableFront && IsPeerRequest(context, replicas), leader.LocalSimulationAsync);

    // Peer requests are direct, so forwarded headers are neither needed nor trusted here.
    internal static bool IsPeerRequest(HttpContext context, IReplicasService replicas)
    {
        var remote = context.Connection.RemoteIpAddress?.MapToIPv6();
        return remote is not null && replicas.Deployments.SlimFaas.Pods.Any(p =>
            IPAddress.TryParse(p.Ip, out var address) && address.MapToIPv6().Equals(remote));
    }

    private static async Task HandleSimulation(HttpContext context, bool allowed,
        Func<ScalingSimulationRequest, CancellationToken, Task<ScalingSimulationResponse>> simulate)
    {
        if (!allowed) { context.Response.StatusCode = 404; return; }
        if (!context.Request.HasJsonContentType()) { await Error(context, 415, "A JSON request is required."); return; }
        if (context.Request.ContentLength > 64 * 1024) { await Error(context, 413, "Simulation requests are limited to 64 KiB."); return; }
        try
        {
            // Also enforce the limit for chunked requests, before JSON deserialization.
            using var body = new MemoryStream();
            byte[] chunk = new byte[8192];
            int count;
            while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (body.Length + count > 64 * 1024) { await Error(context, 413, "Simulation requests are limited to 64 KiB."); return; }
                body.Write(chunk, 0, count);
            }
            var request = JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, (int)body.Length), ScalingJsonContext.Default.ScalingSimulationRequest);
            if (request is null || !ValidFunction(request.Function)) { await Error(context, 400, "A configured function is required."); return; }
            var response = await simulate(request, context.RequestAborted);
            await Write(context, response, ScalingJsonContext.Default.ScalingSimulationResponse);
        }
        catch (JsonException) { await Error(context, 400, "Invalid simulation JSON."); }
        catch (ScalingSimulationException e) { await Error(context, e.StatusCode, e.Message); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    }

    private static Task Error(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        if (status == 429) context.Response.Headers.RetryAfter = "3";
        return Write(context, new ScalingError(message), ScalingJsonContext.Default.ScalingError);
    }

    private static Task Write<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.ContentType = "application/json";
        return JsonSerializer.SerializeAsync(context.Response.Body, value, type, context.RequestAborted);
    }
}
