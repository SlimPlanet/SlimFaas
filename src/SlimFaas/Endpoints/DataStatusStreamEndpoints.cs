using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;

namespace SlimFaas.Endpoints;

public sealed record DataStatusEntry(string Id, long? ExpiresAtMs, long? SizeBytes);
public sealed record DataStatusSummary(int Sets, int Files, long FileBytes, int UnknownFileSizes, int ExpiringSoon);
public sealed record DataStatusPage(string Kind, long ServerTimeMs, IReadOnlyList<DataStatusEntry> Entries,
    string? NextCursor, int TotalCount, DataStatusSummary Summary, int RefreshIntervalMs = 1000);

public static class DataStatusStreamEndpoints
{
    public static void MapDataStatusStreamEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/status-data-stream", HandleStream)
            .WithName("DataStatusStream")
            .AddEndpointFilter<HostPortEndpointFilter>();

    private static async Task HandleStream(HttpContext context, DataStatusSnapshotCache cache,
        NetworkActivityTracker tracker, IOptions<SlimFaasOptions> options, IOptions<DataOptions> dataOptions,
        IFunctionAccessPolicy accessPolicy)
    {
        var settings = options.Value;
        if (!settings.EnableFront || (!settings.ExposeDataMetadata &&
            dataOptions.Value.DefaultVisibility != FunctionVisibility.Public && !accessPolicy.IsInternalRequest(context)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var query = context.Request.Query;
        string kind = query["kind"].FirstOrDefault() ?? "sets";
        string prefix = query["prefix"].FirstOrDefault() ?? "";
        string after = query["after"].FirstOrDefault() ?? "";
        int limit = 100;
        if ((kind != "sets" && kind != "files") || prefix.Length > 200 || after.Length > 200 ||
            (query.ContainsKey("limit") && (!int.TryParse(query["limit"], out limit) || limit is < 1 or > 500)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!tracker.TryReserveStreamClient())
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "3";
            return;
        }

        var ct = context.RequestAborted;
        try
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.StatusStream.StateIntervalMilliseconds));
            do
            {
                var page = cache.GetPage(kind, prefix, after, limit);
                string json = JsonSerializer.Serialize(page, StatusStreamSerializerContext.Default.DataStatusPage);
                await context.Response.WriteAsync($"event: data_state\ndata: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // A disconnected browser owns no background work or activity channel.
        }
        finally
        {
            tracker.ReleaseStreamClient();
        }
    }
}
