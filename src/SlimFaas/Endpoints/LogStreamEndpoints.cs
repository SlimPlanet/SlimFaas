using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Logs;
using SlimFaas.Options;

namespace SlimFaas.Endpoints;

public static class LogStreamEndpoints
{
    public static void MapLogStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/status-log-sources", Sources).AddEndpointFilter<HostPortEndpointFilter>();
        app.MapGet("/status-logs-stream", StreamLogs).AddEndpointFilter<HostPortEndpointFilter>();
    }

    private static bool Allowed(SlimFaasOptions settings) => settings.EnableFront && settings.ExposeLogs;

    private static async Task<IResult> Sources(HttpContext context, IOptions<SlimFaasOptions> options,
        IInstanceLogProvider provider)
    {
        if (!Allowed(options.Value)) return Results.Json(new LogSources("Disabled", []), LogJsonContext.Default.LogSources, statusCode: 403);
        var query = context.Request.Query;
        var target = new LogTarget(query["kind"].ToString(), query["name"].ToString(),
            string.IsNullOrEmpty(query["replica"]) ? null : query["replica"].ToString());
        if (!LogSourceIds.ValidTarget(target)) return Results.BadRequest();
        try { return Results.Json(await provider.GetSourcesAsync(target, context.RequestAborted), LogJsonContext.Default.LogSources); }
        catch (UnauthorizedAccessException) { return Results.Json(new LogSources("Access denied", []), LogJsonContext.Default.LogSources, statusCode: 403); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return Results.Empty; }
        catch (Exception) { return Results.StatusCode(503); }
    }

    private static async Task StreamLogs(HttpContext context, IOptions<SlimFaasOptions> options,
        IInstanceLogProvider provider, NetworkActivityTracker tracker, LogStreamHub hub)
    {
        if (!Allowed(options.Value)) { await DeniedAsync(context, "Disabled"); return; }
        var source = context.Request.Query["source"].ToString();
        int tail = LogLimits.Lines;
        if (context.Request.Query.TryGetValue("tail", out var value) &&
            (!int.TryParse(value, out tail) || tail is < 1 or > LogLimits.Lines))
        { context.Response.StatusCode = 400; return; }
        var ct = context.RequestAborted;
        try
        {
            var key = LogSourceIds.Parse(source);
            if (!(await provider.GetSourcesAsync(key.Target, ct)).Sources.Any(s => s.Id == source))
            { context.Response.StatusCode = 404; return; }
        }
        catch (ArgumentException) { context.Response.StatusCode = 400; return; }
        catch (UnauthorizedAccessException) { await DeniedAsync(context, "Access denied"); return; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception) { context.Response.StatusCode = 503; return; }

        if (!tracker.TryReserveStreamClient()) { Limited(context); return; }
        try
        {
            using var subscription = hub.Subscribe(source);
            if (subscription is null) { Limited(context); return; }
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            long cursor = -1;
            LogState? previousState = null;
            long heartbeat = Environment.TickCount64;
            while (!ct.IsCancellationRequested)
            {
                var (state, lines, next) = subscription.Read(cursor, tail);
                if (state != previousState)
                    await context.Response.WriteAsync($"event: log_state\ndata: {JsonSerializer.Serialize(state, LogJsonContext.Default.LogState)}\n\n", ct);
                if (lines.Length > 0)
                    await context.Response.WriteAsync($"event: log_batch\ndata: {JsonSerializer.Serialize(new LogBatch(lines), LogJsonContext.Default.LogBatch)}\n\n", ct);
                if (Environment.TickCount64 - heartbeat >= 10_000)
                { await context.Response.WriteAsync(": heartbeat\n\n", ct); heartbeat = Environment.TickCount64; }
                if (lines.Length > 0 || state != previousState || Environment.TickCount64 - heartbeat < 200)
                    await context.Response.Body.FlushAsync(ct);
                previousState = state;
                if (lines.Length > 0 || cursor >= 0) cursor = next;
                if (lines.Length == 0 && state.Status is not ("Live" or "Connecting")) break;
                if (lines.Length < 200) await Task.Delay(200, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) { /* The viewer disconnected. */ }
        finally { tracker.ReleaseStreamClient(); }
    }

    private static async Task DeniedAsync(HttpContext context, string status)
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new LogSources(status, []), LogJsonContext.Default.LogSources), context.RequestAborted);
    }

    private static void Limited(HttpContext context)
    { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "3"; }
}
