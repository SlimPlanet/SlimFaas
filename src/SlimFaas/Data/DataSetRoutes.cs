using System.Collections.Immutable;
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

    // Doit matcher SlimDataInterpreter
    private const string TimeToLiveSuffix = "${slimfaas-timetolive}$";

    private static string DataKey(string id) => $"{SetPrefix}{id}";
    private static string TtlKey(string key) => key + TimeToLiveSuffix;

    public static IEndpointRouteBuilder MapDataSetRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/data/sets", Handlers.PostAsync);
        app.MapGet("/data/sets/{id}", Handlers.GetAsync);
        app.MapGet("/data/sets", Handlers.ListAsync);
        app.MapDelete("/data/sets/{id}", Handlers.DeleteAsync);
        return app;
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

            await db.SetAsync(key, bytes ?? Array.Empty<byte>(), ttl).ConfigureAwait(false);

            return Results.Ok(elementId);
        }

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

            foreach (var kv in keyValues)
            {
                var key = kv.Key;

                // on ne liste que les clés data:set:{id} (pas les ttlKey)
                if (!key.StartsWith(SetPrefix, StringComparison.Ordinal))
                    continue;

                if (key.EndsWith(TimeToLiveSuffix, StringComparison.Ordinal))
                    continue;

                var id = key.Substring(SetPrefix.Length);
                if (string.IsNullOrWhiteSpace(id) || !IdValidator.IsSafeId(id))
                    continue;

                var ttlKey = TtlKey(key);

                long expireAtUtcTicks = -1;
                if(keyValues.TryGetValue(ttlKey, out var ttlBytes))
                    SlimDataExpirationCleaner.TryReadInt64(ttlBytes, out expireAtUtcTicks);

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

