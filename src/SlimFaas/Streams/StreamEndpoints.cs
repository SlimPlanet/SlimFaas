using System.Buffers;
using System.Globalization;
using System.Text;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Commands;
using SlimFaas.Endpoints;
using SlimFaas.Options;

namespace SlimFaas.Streams;

public static class StreamEndpoints
{
    private const int MaximumBodyBytes = 14 * 1024 * 1024;
    public static void MapStreamEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/data/streams").AddEndpointFilter<HostPortEndpointFilter>();
        group.MapGet("", Catalog);
        group.MapPost("/{stream}", AppendRoot).DisableAntiforgery();
        group.MapGet("/{stream}", Messages);
        group.MapDelete("/{stream}", Delete);
        group.MapGet("/{stream}/metadata", Metadata);
        group.MapGet("/{stream}/messages", Messages);
        group.MapGet("/{stream}/messages/{messageId}", Message);
        group.MapGet("/{stream}/messages/{messageId}/body", Body);
        group.MapGet("/{stream}/functions", Functions);
        group.MapGet("/{stream}/functions/{function}", Function);
        group.MapPost("/{stream}/functions/{function}", Subscribe);
        group.MapDelete("/{stream}/functions/{function}", Unsubscribe);
        group.MapPost("/{stream}/functions/{function}/read", Reserve);
        group.MapPost("/{stream}/functions/{function}/ack/{messageId}", Ack);
        endpoints.MapPost("/publish-stream/{stream}/{**functionPath}", Append)
            .AddEndpointFilter<HostPortEndpointFilter>().DisableAntiforgery();
        endpoints.MapPost("/publish-stream/{stream}", AppendRoot)
            .AddEndpointFilter<HostPortEndpointFilter>().DisableAntiforgery();
    }

    private static Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<StreamApiError> Error(int status, string message, string? earliest = null) =>
        TypedResults.Json(new StreamApiError(message, earliest), StreamJsonContext.Default.StreamApiError, statusCode: status);
    private static bool Paging(HttpContext context, out int limit)
    {
        limit = 100;
        return !context.Request.Query.TryGetValue("limit", out var value) ||
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= 500;
    }
    private static string EncodeCursor(string scope, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(scope + "\n" + key));
    private static bool Cursor(HttpContext context, string scope, out string? key)
    {
        key = null;
        string raw = context.Request.Query["after"].ToString();
        if (raw.Length == 0) return true;
        if (raw.Length > 2048) return false;
        try
        {
            string value = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(raw));
            if (!value.StartsWith(scope + "\n", StringComparison.Ordinal)) return false;
            key = value[(scope.Length + 1)..];
            return DurableStreamStore.IsSafeName(key);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
    }
    private static async Task<(SlimDataPayload Payload, DurableStreamDefinition? Definition)> Read(
        string stream, HttpContext context, IStreamService service, CancellationToken cancellationToken)
    {
        var payload = await service.ReadAsync(cancellationToken);
        var definition = DurableStreamStore.IsSafeName(stream) ? DurableStreamStore.Definition(payload, stream) : null;
        return (payload, definition is not null && service.CanAccess(context, definition) ? definition : null);
    }
    private static Task<IResult> AppendRoot(string stream, HttpContext context, IStreamService service,
        IOptions<DataOptions> options, CancellationToken cancellationToken) =>
        Append(stream, "", context, service, options, cancellationToken);

    private static async Task<IResult> Append(string stream, string? functionPath, HttpContext context,
        IStreamService service, IOptions<DataOptions> options, CancellationToken cancellationToken)
    {
        if (!DurableStreamStore.IsSafeName(stream))
            return Error(400, "Invalid Stream name.");
        var payload = await service.ReadAsync(cancellationToken);
        var definition = DurableStreamStore.Definition(payload, stream);
        if (definition is null)
        {
            var candidate = new DurableStreamDefinition { Name = stream, Visibility = options.Value.DefaultVisibility.ToString() };
            if (!service.CanAccess(context, candidate)) return TypedResults.NotFound();
            var created = await service.MutateAsync(new()
            {
                Action = DurableStreamAction.Configure, Stream = stream, Visibility = candidate.Visibility
            }, cancellationToken);
            if (created.Status >= 400) return Error(created.Status, created.Error!);
        }
        else if (!service.CanAccess(context, definition)) return TypedResults.NotFound();
        if (context.Request.ContentLength > MaximumBodyBytes)
            return Error(413, "Stream bodies are limited to 14 MiB. Reference a Data File for larger payloads.");
        using var buffer = new MemoryStream();
        byte[] chunk = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            int length;
            while ((length = await context.Request.Body.ReadAsync(chunk.AsMemory(0, 65536), cancellationToken)) > 0)
            {
                if (buffer.Length + length > MaximumBodyBytes)
                    return Error(413, "Stream bodies are limited to 14 MiB. Reference a Data File for larger payloads.");
                await buffer.WriteAsync(chunk.AsMemory(0, length), cancellationToken);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
        var headers = context.Request.Headers.Select(item => new CustomHeader(item.Key, item.Value.ToArray())).ToList();
        var request = new CustomRequest(headers, buffer.ToArray(), "", "/" + (functionPath ?? "").TrimStart('/'),
            "POST", context.Request.QueryString.ToUriComponent());
        var result = await service.MutateAsync(new()
        {
            Action = DurableStreamAction.Append, Stream = stream, Value = MemoryPackSerializer.Serialize(request),
            ContentType = context.Request.ContentType ?? "application/octet-stream", BodyLength = buffer.Length
        }, cancellationToken);
        if (result.Status >= 400) return Error(result.Status, result.Error!);
        context.Response.Headers.Location = $"/data/streams/{Uri.EscapeDataString(stream)}/messages/{result.MessageId}";
        return TypedResults.Json(new StreamAppendReceipt(stream, result.MessageId!),
            StreamJsonContext.Default.StreamAppendReceipt, statusCode: 201);
    }

    private static async Task<IResult> Catalog(HttpContext context, IStreamService service, CancellationToken cancellationToken)
    {
        string prefix = context.Request.Query["prefix"].ToString();
        if (prefix.Length > 256 || !Paging(context, out int limit) || !Cursor(context, "catalog:" + prefix, out string? after))
            return Error(400, "Invalid pagination.");
        var payload = await service.ReadAsync(cancellationToken);
        var rows = service.Summaries(context, payload).Where(item => item.Name.StartsWith(prefix, StringComparison.Ordinal) &&
            (after is null || string.CompareOrdinal(item.Name, after) > 0)).Take(limit + 1).ToArray();
        return TypedResults.Json(new StreamPage<StreamSummary>(rows.Take(limit).ToArray(),
            rows.Length > limit ? EncodeCursor("catalog:" + prefix, rows[limit - 1].Name) : null,
            DateTimeOffset.UtcNow, rows.Select(item => item.Revision).DefaultIfEmpty().Max()),
            StreamJsonContext.Default.StreamPageStreamSummary);
    }
    private static async Task<IResult> Metadata(string stream, HttpContext context, IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        var summary = service.Summaries(context, payload).Single(item => item.Name == stream);
        return TypedResults.Json(summary, StreamJsonContext.Default.StreamSummary);
    }
    private static async Task<IResult> Messages(string stream, HttpContext context, IStreamService service, CancellationToken cancellationToken)
    {
        if (!Paging(context, out int limit)) return Error(400, "Invalid limit; use 1 to 500.");
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        string after = context.Request.Query["after"].ToString();
        long sequence = definition.Earliest - 1;
        if (after.Length > 0)
        {
            string prefix = definition.Generation + "_";
            if (!after.StartsWith(prefix, StringComparison.Ordinal) ||
                !long.TryParse(after.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) ||
                sequence < definition.Earliest - 1)
            {
                string? earliest = DurableStreamStore.Messages(payload, definition, definition.Earliest - 1, 1).FirstOrDefault()?.Id;
                return Error(410, "History is outside the retained Stream generation or boundary.", earliest);
            }
            if (sequence > definition.Tail) return Error(400, "Message position exceeds the Stream tail.");
        }
        var rows = DurableStreamStore.Messages(payload, definition, sequence, limit + 1).ToArray();
        return TypedResults.Json(new StreamPage<StreamMessageMetadata>(
            rows.Take(limit).Select(item => ToMetadata(item, definition.Revision)).ToArray(),
            rows.Length > limit ? rows[limit - 1].Id : null, DateTimeOffset.UtcNow, definition.Revision),
            StreamJsonContext.Default.StreamPageStreamMessageMetadata);
    }
    private static StreamMessageMetadata ToMetadata(DurableStreamMessage message, long revision) =>
        new(message.Id, StreamService.Time(message.AppendedAtTicks), message.ContentType, message.BodyLength, DateTimeOffset.UtcNow, revision);
    private static async Task<IResult> Message(string stream, string messageId, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        var message = DurableStreamStore.Message(payload, stream, messageId);
        return message is null ? Error(410, "Message is unavailable or has left retention.") :
            TypedResults.Json(ToMetadata(message, definition.Revision), StreamJsonContext.Default.StreamMessageMetadata);
    }
    private static async Task<IResult> Body(string stream, string messageId, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        var message = DurableStreamStore.Message(payload, stream, messageId);
        if (message is null) return Error(410, "Message is unavailable or has left retention.");
        var request = MemoryPackSerializer.Deserialize<CustomRequest>(DurableStreamStore.Body(payload, stream, messageId).Span);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        return TypedResults.File(request.Body ?? [], "application/octet-stream", fileDownloadName: messageId + ".bin",
            enableRangeProcessing: true);
    }
    private static async Task<IResult> Functions(string stream, HttpContext context, IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        string scope = stream + ":" + definition.Generation;
        if (!Paging(context, out int limit) || !Cursor(context, scope, out string? after)) return Error(400, "Invalid pagination.");
        var rows = service.Functions(context, payload, definition).Where(item =>
            after is null || string.CompareOrdinal(item.Function, after) > 0).Take(limit + 1).ToArray();
        return TypedResults.Json(new StreamPage<StreamFunctionSummary>(rows.Take(limit).ToArray(),
            rows.Length > limit ? EncodeCursor(scope, rows[limit - 1].Function) : null, DateTimeOffset.UtcNow, definition.Revision),
            StreamJsonContext.Default.StreamPageStreamFunctionSummary);
    }
    private static async Task<IResult> Function(string stream, string function, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        var row = service.Functions(context, payload, definition).FirstOrDefault(item => item.Function == function);
        return row is null ? TypedResults.NotFound() : TypedResults.Json(row, StreamJsonContext.Default.StreamFunctionSummary);
    }
    private static async Task<IResult> Subscribe(string stream, string function, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        var target = service.Function(function);
        if (definition is null || target is null || !service.CanObserveFunction(context, function)) return TypedResults.NotFound();
        if (DurableStreamStore.Subscription(payload, stream, function)?.Active == true)
            return TypedResults.NoContent();
        var result = await service.MutateAsync(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = stream, Function = function,
            FunctionVisibility = target.Visibility.ToString(),
            MaximumParallel = target.NumberParallelRequest, MaximumPerReplica = target.NumberParallelRequestPerPod
        }, cancellationToken);
        return result.Status >= 400 ? Error(result.Status, result.Error!) : TypedResults.NoContent();
    }
    private static async Task<IResult> Unsubscribe(string stream, string function, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null || !service.CanObserveFunction(context, function) ||
            DurableStreamStore.Subscription(payload, stream, function) is null)
            return TypedResults.NotFound();
        var result = await service.MutateAsync(new()
        {
            Action = DurableStreamAction.Unsubscribe, Stream = stream, Function = function
        }, cancellationToken);
        return result.Status >= 400 ? Error(result.Status, result.Error!) : TypedResults.NoContent();
    }

    private static async Task<IResult> Reserve(string stream, string function, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (_, definition) = await Read(stream, context, service, cancellationToken);
        var target = service.Function(function);
        if (definition is null || target is null || !service.CanObserveFunction(context, function)) return TypedResults.NotFound();
        var retry = target.Configuration.DefaultAsync;
        var result = await service.MutateAsync(new()
        {
            Action = DurableStreamAction.Reserve, Stream = stream, Function = function, Owner = "api",
            Replica = "api", MaximumParallel = target.NumberParallelRequest, MaximumPerReplica = target.NumberParallelRequestPerPod,
            TimeoutSeconds = retry.HttpTimeout, Retries = retry.TimeoutRetries.ToArray(), RetryStatuses = retry.HttpStatusRetries.ToArray()
        }, cancellationToken);
        if (result.Status >= 400) return Error(result.Status, result.Error!);
        return result.Delivery is not { } attempt ? TypedResults.NoContent() :
            TypedResults.Json(new StreamReservation(stream, function, attempt.MessageId, attempt.ElementId, attempt.Attempt,
                StreamService.Time(attempt.DeadlineTicks)), StreamJsonContext.Default.StreamReservation);
    }
    private static async Task<IResult> Ack(string stream, string function, string messageId, HttpContext context,
        IStreamService service, CancellationToken cancellationToken)
    {
        var (payload, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null || !service.CanObserveFunction(context, function)) return TypedResults.NotFound();
        string element = context.Request.Headers[SlimQueuesWorker.SlimfaasElementId].ToString();
        if (element.Length == 0) return Error(400, "SlimFaas-Element-Id from the reservation is required.");
        var attempt = DurableStreamStore.Attempt(payload, element);
        if (attempt is null || attempt.Stream != stream || attempt.Function != function || attempt.MessageId != messageId)
            return TypedResults.NotFound();
        var result = await service.MutateAsync(new()
        {
            Action = DurableStreamAction.Complete, Stream = stream, Function = function, ElementId = element, StatusCode = 200
        }, cancellationToken);
        return result.Status >= 400 ? Error(result.Status, result.Error!) : TypedResults.NoContent();
    }
    private static async Task<IResult> Delete(string stream, HttpContext context, IStreamService service, CancellationToken cancellationToken)
    {
        var (_, definition) = await Read(stream, context, service, cancellationToken);
        if (definition is null) return TypedResults.NotFound();
        var result = await service.MutateAsync(new() { Action = DurableStreamAction.Delete, Stream = stream }, cancellationToken);
        return result.Status >= 400 ? Error(result.Status, result.Error!) : TypedResults.NoContent();
    }
}
