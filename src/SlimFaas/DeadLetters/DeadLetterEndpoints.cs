using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using MemoryPack;
using SlimData;
using SlimFaas.Kubernetes;
using SlimFaas.Endpoints;
using SlimFaas.WebSocket;

namespace SlimFaas.DeadLetters;

/// <summary>A bounded page of DLQ summaries.</summary>
public sealed record DeadLetterCatalogPage(IReadOnlyList<DeadLetterSummary> Items, string? NextCursor, DateTimeOffset CapturedAt, long Revision);
/// <summary>A bounded page of retained failure metadata.</summary>
public sealed record DeadLetterMessagePage(IReadOnlyList<DeadLetterMessage> Items, string? NextCursor, DateTimeOffset CapturedAt, long Revision);
/// <summary>Original HTTP request information, loaded without its body.</summary>
public sealed record DeadLetterRequest(string Method, string Path, string QueryString, string ContentType, long? BodyLength, IReadOnlyList<CustomHeader>? Headers);
/// <summary>Retained failure and its replay lineage.</summary>
public sealed record DeadLetterMessage(string Id, string Function, string OriginalElementId, DateTimeOffset AcceptedAt,
    DateTimeOffset FailedAt, DateTimeOffset ExpiresAt, int Attempts, string FailureReason, int? LastHttpStatus,
    string? ReplayOfDeadLetterId, string? RootElementId, DeadLetterRequest Request);
/// <summary>Confirms a durable insertion, not application success.</summary>
public sealed record DeadLetterReplay(string DeadLetterId, string OriginalElementId, string ElementId, string Function);

[JsonSerializable(typeof(DeadLetterCatalogPage))]
[JsonSerializable(typeof(DeadLetterMessagePage))]
[JsonSerializable(typeof(DeadLetterMessage))]
[JsonSerializable(typeof(DeadLetterSummary))]
[JsonSerializable(typeof(DeadLetterReplay))]
internal partial class DeadLetterJsonContext : JsonSerializerContext;

public static class DeadLetterEndpoints
{
    private static readonly HashSet<string> Reasons = new(StringComparer.Ordinal)
    {
        "RetriesExhausted", "NonRetryableResponse", "DeliveryTimeout", "CallbackError", "CallbackTimeout", "TransportError"
    };

    public static void MapDeadLetterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/data/dead-letters").AddEndpointFilter<HostPortEndpointFilter>()
            .AddEndpointFilter(async (context, next) =>
                context.HttpContext.RequestServices.GetRequiredService<IDeadLetterService>().CanAccess(context.HttpContext)
                    ? await next(context) : TypedResults.NotFound());
        group.MapGet("", Catalog);
        group.MapGet("/{function}", Summary);
        group.MapGet("/{function}/messages", Messages);
        group.MapGet("/{function}/messages/{messageId}", Detail);
        group.MapGet("/{function}/messages/{messageId}/body", Body);
        group.MapPost("/{function}/messages/{messageId}/requeue", Requeue);
        group.MapDelete("/{function}/messages/{messageId}", Delete);
        group.MapGet("/-/metrics", Metrics);
    }

    private static async Task<IResult> Metrics(IDeadLetterService service, CancellationToken cancellationToken)
    {
        var snapshot = await service.ReadAsync(cancellationToken);
        var text = new StringBuilder("# TYPE slimfaas_dead_letters gauge\n# TYPE slimfaas_dead_letter_transitions_total counter\n");
        foreach (var summary in snapshot.Summaries)
            text.Append(CultureInfo.InvariantCulture, $"slimfaas_dead_letters{{function=\"{Label(summary.Function)}\"}} {summary.Count}\n");
        foreach (string key in snapshot.State.KeyValues.Keys.Where(key => key.StartsWith(DeadLetterKeys.PolicyPrefix, StringComparison.Ordinal)))
        {
            string function = key[DeadLetterKeys.PolicyPrefix.Length..];
            if (!snapshot.State.KeyValues.TryGetValue(DeadLetterKeys.Stats(function), out var bytes))
                continue;
            var stats = MemoryPackSerializer.Deserialize<DeadLetterStatistics>(bytes.Span)!;
            foreach (var (outcome, count) in new (string, long)[] { ("captured", stats.Captured), ("requeued", stats.Requeued),
                ("deleted", stats.Deleted), ("expired", stats.Expired), ("evicted", stats.Evicted) })
                text.Append(CultureInfo.InvariantCulture, $"slimfaas_dead_letter_transitions_total{{function=\"{Label(function)}\",outcome=\"{outcome}\"}} {count}\n");
        }
        text.Append(CultureInfo.InvariantCulture, $"# TYPE slimfaas_dead_letter_persistence_failures_total counter\nslimfaas_dead_letter_persistence_failures_total {snapshot.PersistenceFailures}\n");
        return TypedResults.Text(text.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    private static string Label(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static bool Valid(string function, string? messageId = null) =>
        IdValidator.IsSafeId(function) && (messageId is null || IdValidator.IsSafeId(messageId));

    private static bool Paging(HttpContext context, out int limit, out string? cursor)
    {
        limit = 100;
        cursor = null;
        if (context.Request.Query.TryGetValue("limit", out var value) &&
            (!int.TryParse(value, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 500))
            return false;
        string? after = context.Request.Query["after"];
        if (string.IsNullOrEmpty(after))
            return true;
        if (after.Length > 2048)
            return false;
        try { cursor = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(after)); }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
        return !string.IsNullOrEmpty(cursor);
    }

    private static string Cursor(string key) => Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

    private static async Task<IResult> Catalog(HttpContext context, IDeadLetterService service, CancellationToken cancellationToken)
    {
        if (!Paging(context, out int limit, out string? cursor))
            return TypedResults.BadRequest("Invalid pagination.");
        string prefix = context.Request.Query["prefix"].ToString();
        if (prefix.Length > 256)
            return TypedResults.BadRequest("Invalid prefix.");
        var snapshot = await service.ReadAsync(cancellationToken);
        var rows = snapshot.Summaries.Where(item => item.Function.StartsWith(prefix, StringComparison.Ordinal) &&
            (cursor is null || string.CompareOrdinal(item.Function, cursor) > 0)).Take(limit + 1).ToList();
        return TypedResults.Json(new DeadLetterCatalogPage(rows.Take(limit).ToArray(),
            rows.Count > limit ? Cursor(rows[limit - 1].Function) : null, snapshot.CapturedAt, snapshot.Revision),
            DeadLetterJsonContext.Default.DeadLetterCatalogPage);
    }

    private static async Task<IResult> Summary(string function, IDeadLetterService service, CancellationToken cancellationToken)
    {
        if (!Valid(function))
            return TypedResults.BadRequest("Invalid function.");
        var snapshot = await service.ReadAsync(cancellationToken);
        var summary = snapshot.Summaries.FirstOrDefault(item => item.Function == function);
        return summary is null ? TypedResults.NotFound() : TypedResults.Json(summary, DeadLetterJsonContext.Default.DeadLetterSummary);
    }

    private static async Task<IResult> Messages(string function, HttpContext context, IDeadLetterService service, CancellationToken cancellationToken)
    {
        if (!Valid(function) || !Paging(context, out int limit, out string? cursor))
            return TypedResults.BadRequest("Invalid identifier or pagination.");
        string? reason = context.Request.Query["reason"];
        string? statusValue = context.Request.Query["httpStatus"];
        int? status = null;
        if (!string.IsNullOrEmpty(reason) && !Reasons.Contains(reason))
            return TypedResults.BadRequest("Invalid reason.");
        if (!string.IsNullOrEmpty(statusValue))
        {
            if (!int.TryParse(statusValue, CultureInfo.InvariantCulture, out int parsed) || parsed is < 100 or > 599)
                return TypedResults.BadRequest("Invalid HTTP status.");
            status = parsed;
        }
        long ticks = 0;
        string lastId = "";
        if (cursor is not null)
        {
            int separator = cursor.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || !long.TryParse(cursor.AsSpan(0, separator), CultureInfo.InvariantCulture, out ticks) ||
                ticks < 0 || !IdValidator.IsSafeId(cursor[(separator + 1)..]))
                return TypedResults.BadRequest("Invalid cursor.");
            lastId = cursor[(separator + 1)..];
        }
        var snapshot = await service.ReadAsync(cancellationToken);
        var entries = DeadLetterService.Entries(snapshot.State, function)
            .Where(item => item.ExpiresAtTicks > snapshot.CapturedAt.UtcTicks &&
                (string.IsNullOrEmpty(reason) || item.FailureReason == reason) &&
                (status is null || item.LastHttpStatus == status) &&
                (cursor is null || item.FailedAtTicks > ticks || item.FailedAtTicks == ticks && string.CompareOrdinal(item.Id, lastId) > 0))
            .OrderBy(item => item.FailedAtTicks).ThenBy(item => item.Id, StringComparer.Ordinal).Take(limit + 1).ToList();
        var messages = entries.Take(limit).Select(item => Project(item, snapshot.State, false)).ToArray();
        return TypedResults.Json(new DeadLetterMessagePage(messages, entries.Count > limit
            ? Cursor(entries[limit - 1].FailedAtTicks.ToString(CultureInfo.InvariantCulture) + ":" + entries[limit - 1].Id) : null,
            snapshot.CapturedAt, snapshot.Revision), DeadLetterJsonContext.Default.DeadLetterMessagePage);
    }

    private static DeadLetterMessage Project(DeadLetterEntry entry, SlimData.Commands.SlimDataPayload state, bool details)
    {
        DeadLetterService.WithPayload(state, entry);
        var request = MemoryPackSerializer.Deserialize<CustomRequest>(entry.Value);
        string contentType = request.Headers.FirstOrDefault(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Values?.FirstOrDefault() ?? "application/octet-stream";
        long? length = request.Body?.LongLength ?? 0;
        if (!string.IsNullOrEmpty(request.OffloadedFileId))
        {
            length = state.KeyValues.TryGetValue(DataFileKeys.MetaKey(request.OffloadedFileId), out var metadata)
                ? MemoryPackSerializer.Deserialize<DataSetMetadata>(metadata.Span)?.Length : null;
        }
        return new(entry.Id, entry.Function, entry.OriginalElementId, DeadLetterService.Time(entry.AcceptedAtTicks),
            DeadLetterService.Time(entry.FailedAtTicks), DeadLetterService.Time(entry.ExpiresAtTicks), entry.Attempts,
            entry.FailureReason, entry.LastHttpStatus, entry.ReplayOfDeadLetterId, entry.RootElementId,
            new(request.Method, request.Path, details ? request.Query : "", contentType, length, details ? request.Headers : null));
    }

    private static DeadLetterEntry? Find(DeadLetterSnapshot snapshot, string function, string messageId) =>
        DeadLetterService.Entries(snapshot.State, function)
            .Where(item => item.Id == messageId && item.ExpiresAtTicks > snapshot.CapturedAt.UtcTicks)
            .Select(item => DeadLetterService.WithPayload(snapshot.State, item)).FirstOrDefault();

    private static async Task<IResult> Detail(string function, string messageId, IDeadLetterService service, CancellationToken cancellationToken)
    {
        if (!Valid(function, messageId))
            return TypedResults.BadRequest("Invalid identifier.");
        var snapshot = await service.ReadAsync(cancellationToken);
        var entry = Find(snapshot, function, messageId);
        return entry is null ? TypedResults.NotFound() : TypedResults.Json(Project(entry, snapshot.State, true), DeadLetterJsonContext.Default.DeadLetterMessage);
    }

    private static async Task<IResult> Body(string function, string messageId, HttpContext context, IDeadLetterService service, CancellationToken cancellationToken)
    {
        if (!Valid(function, messageId))
            return TypedResults.BadRequest("Invalid identifier.");
        var snapshot = await service.ReadAsync(cancellationToken);
        var entry = Find(snapshot, function, messageId);
        if (entry is null)
            return TypedResults.NotFound();
        var stream = await service.OpenBodyAsync(entry, cancellationToken);
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return stream is null ? TypedResults.Conflict("The retained payload is unavailable.")
            : TypedResults.Stream(stream, Project(entry, snapshot.State, true).Request.ContentType,
                fileDownloadName: messageId + ".bin", enableRangeProcessing: stream.CanSeek);
    }

    private static async Task<IResult> Requeue(string function, string messageId, IDeadLetterService service,
        IReplicasService replicas, IWebSocketFunctionRepository virtualFunctions, CancellationToken cancellationToken)
    {
        if (!Valid(function, messageId))
            return TypedResults.BadRequest("Invalid identifier.");
        var snapshot = await service.ReadAsync(cancellationToken);
        var entry = Find(snapshot, function, messageId);
        DeadLetterReceipt? receipt = snapshot.State.Hashsets.TryGetValue(DeadLetterKeys.Receipts(function), out var receipts) &&
            receipts.TryGetValue(messageId, out var bytes) ? MemoryPackSerializer.Deserialize<DeadLetterReceipt>(bytes.Span) : null;
        if (receipt?.ExpiresAtTicks > snapshot.CapturedAt.UtcTicks)
            return Accepted(receipt);
        if (entry is null)
            return TypedResults.NotFound();
        var target = replicas.Deployments.Functions.Concat(virtualFunctions.GetVirtualDeployments()).FirstOrDefault(item => item.Deployment == function);
        if (target is null)
            return TypedResults.Conflict("The function no longer exists.");
        await using var body = await service.OpenBodyAsync(entry, cancellationToken);
        if (body is null)
            return TypedResults.Conflict("The retained payload is unavailable.");
        var retry = target.Configuration.DefaultAsync;
        var result = await service.MutateAsync(new()
        {
            Action = DeadLetterAction.Requeue, Function = function, MessageId = messageId,
            NewElementId = Guid.NewGuid().ToString(), RetryTimeoutSeconds = retry.HttpTimeout,
            Retries = retry.TimeoutRetries.ToArray(), HttpStatusRetries = retry.HttpStatusRetries.ToArray()
        });
        return result.Receipt is not null ? Accepted(result.Receipt) : TypedResults.StatusCode(result.Status);
    }

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<DeadLetterReplay> Accepted(DeadLetterReceipt receipt) => TypedResults.Json(
        new DeadLetterReplay(receipt.DeadLetterId, receipt.OriginalElementId, receipt.ElementId, receipt.Function),
        DeadLetterJsonContext.Default.DeadLetterReplay, statusCode: StatusCodes.Status202Accepted);

    private static async Task<IResult> Delete(string function, string messageId, IDeadLetterService service, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Valid(function, messageId))
            return TypedResults.BadRequest("Invalid identifier.");
        var result = await service.MutateAsync(new() { Action = DeadLetterAction.Delete, Function = function, MessageId = messageId });
        return result.Status == 204 ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
