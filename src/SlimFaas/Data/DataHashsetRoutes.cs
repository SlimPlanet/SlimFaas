using System.Collections.Immutable;
using System.Text.Json.Serialization;
using DotNext;
using SlimData.Commands;

namespace SlimFaas;

public static class DataHashsetRoutes
{
    private const string HashsetPrefix = "data:hashset:";
    private const string ValueField = "value";

    // Doit matcher SlimDataInterpreter
    private const string TimeToLiveSuffix = "${slimfaas-timetolive}$";
    private const string HashsetTtlField = "__ttl__";

    private static string HashKey(string id) => $"{HashsetPrefix}{id}";
    private static string TtlKey(string key) => key + TimeToLiveSuffix;

    public static IEndpointRouteBuilder MapDataHashsetRoutes(this IEndpointRouteBuilder app)
    {
        app.MapPost("/data/hashsets", Handlers.PostAsync);
        app.MapGet("/data/hashsets/{id}", Handlers.GetAsync);
        app.MapGet("/data/hashsets", Handlers.ListAsync);
        app.MapDelete("/data/hashsets/{id}", Handlers.DeleteAsync);
        return app;
    }



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

            var key = HashKey(elementId);

            var (bytes, error) = await DataSetRoutes.Handlers.ReadBodyUpTo1MbAsync(ctx, ct).ConfigureAwait(false);
            if (error is not null) return error;

            await db.HashSetAsync(key, new Dictionary<string, byte[]>
            {
                [ValueField] = bytes ?? Array.Empty<byte>()
            }, ttl).ConfigureAwait(false);

            return Results.Ok(elementId);
        }

        public static async Task<IResult> GetAsync(IDatabaseService db, string id)
        {
            if (!IdValidator.IsSafeId(id))
                return Results.BadRequest("Invalid id.");

            var key = HashKey(id);
            var dict = await db.HashGetAllAsync(key).ConfigureAwait(false);

            if (dict is null || !dict.TryGetValue(ValueField, out var bytes))
                return Results.NotFound();

            return Results.Bytes(bytes, "application/octet-stream");
        }

        public static Task<IResult> ListAsync(ISupplier<SlimDataPayload> state)
        {
            var payload = state.Invoke();

            var hashsets = payload.Hashsets
                ?? ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty;

            var list = new List<DataHashsetEntry>(capacity: 128);

            foreach (var hs in hashsets)
            {
                var key = hs.Key;

                // on liste uniquement les hashsets data:hashset:{id} (pas les ttlKey)
                if (!key.StartsWith(HashsetPrefix, StringComparison.Ordinal))
                    continue;

                if (key.EndsWith(TimeToLiveSuffix, StringComparison.Ordinal))
                    continue;

                var id = key.Substring(HashsetPrefix.Length);
                if (string.IsNullOrWhiteSpace(id) || !IdValidator.IsSafeId(id))
                    continue;

                long? expireAtTicks = null;
                var ttlKey = TtlKey(key);

                if (hashsets.TryGetValue(ttlKey, out var meta) &&
                    meta.TryGetValue(HashsetTtlField, out var ttlBytes) &&
                    ttlBytes.Length >= sizeof(long))
                {
                    var t = BitConverter.ToInt64(ttlBytes.Span);
                    if (t > 0) expireAtTicks = t;
                }

                list.Add(new DataHashsetEntry(id, expireAtTicks));
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

            await db.HashSetDeleteAsync(HashKey(id), dictionaryKey: "").ConfigureAwait(false); // delete whole hashset
            return Results.NoContent();
        }
    }
}

public sealed record DataHashsetEntry(string Id, long? ExpireAtUtcTicks);

[JsonSerializable(typeof(List<DataHashsetEntry>))]
public partial class DataHashsetFileRoutesJsonContext : JsonSerializerContext
{
}

