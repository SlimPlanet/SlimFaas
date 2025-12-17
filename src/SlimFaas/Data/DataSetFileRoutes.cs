using System.Collections.Immutable;
using DotNext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SlimData;
using SlimData.Commands;

namespace SlimFaas;

public static class DataSetFileRoutes
{
    private const string SetPrefix = "data:set:";

    // Doit matcher SlimDataInterpreter
    private const string TimeToLiveSuffix = "${slimfaas-timetolive}$";

    private static string DataKey(string id) => $"{SetPrefix}{id}";
    private static string TtlKey(string key) => key + TimeToLiveSuffix;

    public static IEndpointRouteBuilder MapDataSetFileRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/data/set", Handlers.PostAsync);
        app.MapGet("/data/set/{id}", Handlers.GetAsync);
        app.MapGet("/data/set", Handlers.ListAsync);
        app.MapDelete("/data/set/{id}", Handlers.DeleteAsync);
        return app;
    }

    public sealed record DataSetEntry(string Id, long? ExpireAtUtcTicks);

    public static class Handlers
    {
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

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
                bytes = ms.ToArray();
            }

            await db.SetAsync(key, bytes, ttl).ConfigureAwait(false);

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

                long? expireAtTicks = null;
                var ttlKey = TtlKey(key);

                if (keyValues.TryGetValue(ttlKey, out var ttlBytes) && ttlBytes.Length >= sizeof(long))
                {
                    var t = BitConverter.ToInt64(ttlBytes.Span);
                    if (t > 0) expireAtTicks = t;
                }

                list.Add(new DataSetEntry(id, expireAtTicks));
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
