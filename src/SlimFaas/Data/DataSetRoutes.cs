using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using DotNext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SlimData;
using SlimData.Commands;
using SlimData.Expiration;

namespace SlimFaas;

public static class DataSetRoutes
{
    private const string SetPrefix = "data:set:";

    private static string DataKey(string id) => $"{SetPrefix}{id}";
    private static string TtlKey(string key) => key + SlimDataInterpreter.TimeToLivePostfix;

    public static IEndpointRouteBuilder MapDataSetRoutes(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/data/sets")
            .AddEndpointFilter<DataVisibilityEndpointFilter>();
        group.MapPost("", Handlers.PostAsync);
        group.MapPost("/{id}", Handlers.PostAsync);
        group.MapPost("/{id}/incr", Handlers.IncrAsync);
        group.MapPost("/{id}/incrby", Handlers.IncrByAsync);
        group.MapPost("/{id}/incrbyfloat", Handlers.IncrByFloatAsync);
        group.MapPost("/{id}/decr", Handlers.DecrAsync);
        group.MapPost("/{id}/decrby", Handlers.DecrByAsync);
        group.MapGet("/{id}", Handlers.GetAsync);
        group.MapGet("", Handlers.ListAsync);
        group.MapDelete("/{id}", Handlers.DeleteAsync);
        return endpoints;
    }


    public static class Handlers
    {

    private const int MaxBodyBytes = 1 * 1024 * 1024; // 1 MiB = 1_048_576 bytes

    private static IResult PayloadTooLarge() =>
        Results.Problem(
            title: "Payload too large",
            detail: $"Max allowed size is {MaxBodyBytes} bytes (1 MiB).",
            statusCode: StatusCodes.Status413PayloadTooLarge);

    public static async Task<(byte[]? Bytes, IResult? Error)> ReadBodyUpTo1MbAsync(
        HttpContext ctx,
        CancellationToken ct)
    {
        if (ctx.Request.ContentLength is long len && len > MaxBodyBytes)
            return (null, PayloadTooLarge());

        using var ms = new MemoryStream(capacity: (int)Math.Min(ctx.Request.ContentLength ?? MaxBodyBytes, MaxBodyBytes));
        var buffer = new byte[64 * 1024];

        long total = 0;
        while (true)
        {
            var read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;

            total += read;
            if (total > MaxBodyBytes)
                return (null, PayloadTooLarge());

            ms.Write(buffer, 0, read);
        }

        return (ms.ToArray(), null);
    }


        public static async Task<IResult> PostAsync(
            HttpContext ctx,
            IDatabaseService db,
            string? id,
            long? ttl,
            CancellationToken ct)
        {
            var elementId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id!;

            if (!IdValidator.IsSafeId(elementId))
                return Results.BadRequest("Invalid id.");

            var key = DataKey(elementId);

             var (bytes, error) = await ReadBodyUpTo1MbAsync(ctx, ct).ConfigureAwait(false);
             if (error is not null) return error;

            try
            {
                await db.SetAsync(key, bytes ?? Array.Empty<byte>(), ttl).ConfigureAwait(false);
            }
            catch (BatchQueueFullException ex)
            {
                return CapacityError(ex);
            }
            catch (BatchItemTooLargeException ex)
            {
                return CapacityError(ex);
            }
            catch (SlimDataUnavailableException ex)
            {
                return Unavailable(ex);
            }

            return Results.Ok(elementId);
        }

        public static Task<IResult> IncrAsync(IDatabaseService db, string id, long? ttl = null) =>
            IntegerMutationAsync(db, id, 1, ttl);

        public static async Task<IResult> IncrByAsync(IDatabaseService db, string id, long? by, long? ttl = null)
        {
            if (!by.HasValue)
                return Results.BadRequest("Missing by.");

            return await IntegerMutationAsync(db, id, by.Value, ttl).ConfigureAwait(false);
        }

        public static async Task<IResult> DecrAsync(IDatabaseService db, string id, long? ttl = null) =>
            await IntegerMutationAsync(db, id, -1, ttl).ConfigureAwait(false);

        public static async Task<IResult> DecrByAsync(IDatabaseService db, string id, long? by, long? ttl = null)
        {
            if (!by.HasValue)
                return Results.BadRequest("Missing by.");

            long delta;
            try
            {
                delta = checked(-by.Value);
            }
            catch (OverflowException)
            {
                return Conflict("Integer decrement overflow.");
            }

            return await IntegerMutationAsync(db, id, delta, ttl).ConfigureAwait(false);
        }

        public static async Task<IResult> IncrByFloatAsync(IDatabaseService db, string id, decimal? by, long? ttl = null)
        {
            if (!by.HasValue)
                return Results.BadRequest("Missing by.");

            if (!IdValidator.IsSafeId(id))
                return Results.BadRequest("Invalid id.");

            if (ttl.HasValue && ttl.Value <= 0)
                return Results.BadRequest("Invalid ttl.");

            KeyValueCommandResult result;
            try
            {
                result = await db.SetAsync(
                    DataKey(id),
                    timeToLiveMilliseconds: ttl,
                    operation: KeyValueOperation.IncrementFloat,
                    floatDelta: by.Value).ConfigureAwait(false);
            }
            catch (BatchQueueFullException ex)
            {
                return CapacityError(ex);
            }
            catch (BatchItemTooLargeException ex)
            {
                return CapacityError(ex);
            }
            catch (SlimDataUnavailableException ex)
            {
                return Unavailable(ex);
            }

            return ToNumericResult(result, isFloat: true);
        }

        private static async Task<IResult> IntegerMutationAsync(IDatabaseService db, string id, long delta, long? ttl)
        {
            if (!IdValidator.IsSafeId(id))
                return Results.BadRequest("Invalid id.");

            if (ttl.HasValue && ttl.Value <= 0)
                return Results.BadRequest("Invalid ttl.");

            KeyValueCommandResult result;
            try
            {
                result = await db.SetAsync(
                    DataKey(id),
                    timeToLiveMilliseconds: ttl,
                    operation: KeyValueOperation.IncrementInteger,
                    integerDelta: delta).ConfigureAwait(false);
            }
            catch (BatchQueueFullException ex)
            {
                return CapacityError(ex);
            }
            catch (BatchItemTooLargeException ex)
            {
                return CapacityError(ex);
            }
            catch (SlimDataUnavailableException ex)
            {
                return Unavailable(ex);
            }

            return ToNumericResult(result, isFloat: false);
        }

        private static IResult ToNumericResult(KeyValueCommandResult result, bool isFloat)
        {
            if (result.Status == KeyValueCommandStatus.Applied)
            {
                var text = isFloat
                    ? (result.DecimalValue ?? 0m).ToString("G29", CultureInfo.InvariantCulture)
                    : (result.IntegerValue ?? 0L).ToString(CultureInfo.InvariantCulture);

                return Results.Text(text, "text/plain");
            }

            return Conflict(result.ErrorMessage ?? "Key/value command failed.");
        }

        private static IResult Conflict(string detail) =>
            Results.Problem(
                title: "Key/value command failed",
                detail: detail,
                statusCode: StatusCodes.Status409Conflict);

        private static IResult CapacityError(Exception exception)
        {
            var statusCode = exception is BatchItemTooLargeException
                ? StatusCodes.Status413PayloadTooLarge
                : StatusCodes.Status429TooManyRequests;
            return Results.Problem(
                title: statusCode == StatusCodes.Status413PayloadTooLarge ? "Payload too large" : "SlimData is busy",
                detail: exception.Message,
                statusCode: statusCode);
        }

        private static IResult Unavailable(SlimDataUnavailableException exception) =>
            Results.Problem(
                title: "SlimData is unavailable",
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);

        public static async Task<IResult> GetAsync(IDatabaseService db, string id)
        {
            if (!IdValidator.IsSafeId(id))
                return Results.BadRequest("Invalid id.");

            var key = DataKey(id);
            var bytes = await db.GetAsync(key).ConfigureAwait(false);
            if (bytes is null)
                return Results.NotFound();

            return Results.Bytes(bytes, "application/octet-stream");
        }

        public static Task<IResult> ListAsync(ISupplier<SlimDataPayload> state)
        {
            var payload = state.Invoke();

            var keyValues = payload.KeyValues
                ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;

            var list = new List<DataSetEntry>(capacity: 128);
            var nowTicks = DateTime.UtcNow.Ticks;
            foreach (var kv in keyValues)
            {
                var key = kv.Key;

                // on ne liste que les clés data:set:{id} (pas les ttlKey)
                if (!key.StartsWith(SetPrefix, StringComparison.Ordinal))
                    continue;

                if (key.EndsWith(SlimDataInterpreter.TimeToLivePostfix, StringComparison.Ordinal))
                    continue;

                var id = key.Substring(SetPrefix.Length);
                if (string.IsNullOrWhiteSpace(id) || !IdValidator.IsSafeId(id))
                    continue;

                var ttlKey = TtlKey(key);

                long expireAtUtcTicks = -1;
                if(keyValues.TryGetValue(ttlKey, out var ttlBytes))
                    SlimDataExpirationCleaner.TryReadInt64(ttlBytes, out expireAtUtcTicks);
                if (expireAtUtcTicks > 0 && expireAtUtcTicks <= nowTicks)
                    continue;

                list.Add(new DataSetEntry(id, expireAtUtcTicks));
            }

            list.Sort(static (a, b) =>
            {
                var c = Nullable.Compare(a.ExpireAtUtcTicks, b.ExpireAtUtcTicks); // null d'abord
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });

            return Task.FromResult<IResult>(Results.Ok(list));
        }

        public static async Task<IResult> DeleteAsync(IDatabaseService db, string id)
        {
            if (!IdValidator.IsSafeId(id))
                return Results.BadRequest("Invalid id.");

            await db.DeleteAsync(DataKey(id)).ConfigureAwait(false);
            return Results.NoContent();
        }
    }
}

public sealed record DataSetEntry(string Id, long? ExpireAtUtcTicks);


[JsonSerializable(typeof(List<DataSetEntry>))]
public partial class DataSetFileRoutesRoutesJsonContext : JsonSerializerContext
{
}
