using System.Collections.Immutable;
using DotNext;
using Microsoft.Extensions.Logging;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas;

namespace SlimData.Expiration;

public sealed class SlimDataExpirationCleaner
{

    private readonly ISupplier<SlimDataPayload> _state;
    private readonly IDatabaseService _db;
    private readonly IFileRepository _files;
    private readonly ILogger<SlimDataExpirationCleaner> _logger;

    public SlimDataExpirationCleaner(
        ISupplier<SlimDataPayload> state,
        IDatabaseService db,
        IFileRepository files,
        ILogger<SlimDataExpirationCleaner> logger)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task CleanupOnceAsync(CancellationToken ct)
    {
        var payload = _state.Invoke();

        var keyValues = payload.KeyValues ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
        var hashsets  = payload.Hashsets ?? ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty;

        var nowTicks = DateTime.UtcNow.Ticks;

        // 1) KeyValues TTL: keys finissant par suffix
        foreach (var kv in keyValues)
        {
            ct.ThrowIfCancellationRequested();

            var ttlKey = kv.Key;
            if (!ttlKey.EndsWith(SlimDataInterpreter.TimeToLivePostfix, StringComparison.Ordinal))
                continue;

            if (!TryReadInt64(kv.Value, out var expireAtTicks))
                continue;

            if (expireAtTicks > nowTicks)
                continue;

            var baseKey = ttlKey[..^SlimDataInterpreter.TimeToLivePostfix.Length];
            _logger.LogWarning("Deleting expired keyvalue. key={Key}", baseKey);

            try
            {
                await _db.DeleteAsync(baseKey).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired keyvalue. key={Key}", baseKey); }
        }

        foreach (var hs in hashsets)
        {
            ct.ThrowIfCancellationRequested();

            var key = hs.Key;

            // TTL actuel: stocké dans le hashset principal sous le champ __ttl__
            long? expireAtTicks = TryReadHashsetExpireAt(hs.Value);

            // Fallback legacy: TTL stocké dans hashsets[key+suffix][__ttl__]
            if (expireAtTicks is null)
            {
                continue;
            }

            if (expireAtTicks.Value > nowTicks)
                continue;

            try { await _db.HashSetDeleteAsync(key, "").ConfigureAwait(false); } // delete whole hashset
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired hashset. key={Key}", key); }
        }
        // 3) Local disk TTL: chaque noeud supprime SES fichiers expirés en lisant .meta.json
        await foreach (var entry in _files.EnumerateAllMetadataAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            var exp = entry.Metadata.ExpireAtUtcTicks;
            if (exp is not long t || t <= 0 || t > nowTicks)
                continue;

            _logger.LogWarning("Deleting expired local file by disk metadata. id={Id} expireAt={ExpireAt}", entry.Id, t);
            try { await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired local file. id={Id}", entry.Id); }
        }
    }

    private static long? TryReadHashsetExpireAt(ImmutableDictionary<string, ReadOnlyMemory<byte>> dict)
    {
        if (!dict.TryGetValue(SlimDataInterpreter.HashsetTtlField, out var ttlBytes))
            return null;
        if (!TryReadInt64(ttlBytes, out var t))
            return null;
        return t > 0 ? t : null;
    }

    public static bool TryReadInt64(ReadOnlyMemory<byte> bytes, out long value)
    {
        value = 0;
        if (bytes.Length < sizeof(long)) return false;
        value = BitConverter.ToInt64(bytes.Span);
        return true;
    }

}
