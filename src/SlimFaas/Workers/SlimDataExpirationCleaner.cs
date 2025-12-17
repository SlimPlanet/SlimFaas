using System.Collections.Immutable;
using DotNext;
using Microsoft.Extensions.Logging;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas;

namespace SlimData.Expiration;

public sealed class SlimDataExpirationCleaner
{
    // doit matcher SlimDataInterpreter
    public const string TimeToLiveSuffix = "${slimfaas-timetolive}$";
    public const string HashsetTtlField = "__ttl__";

    private const string FileMetaPrefix = "data:file:";
    private const string FileMetaSuffix = ":meta";

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
            if (!ttlKey.EndsWith(TimeToLiveSuffix, StringComparison.Ordinal))
                continue;

            if (!TryReadInt64(kv.Value, out var expireAtTicks))
                continue;

            if (expireAtTicks > nowTicks)
                continue;

            var baseKey = ttlKey[..^TimeToLiveSuffix.Length];

            if (TryParseFileMetaKey(baseKey, out var elementId))
            {
                try { await _files.DeleteAsync(elementId, ct).ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete local file for expired meta. id={Id}", elementId); }
            }

            try { await _db.DeleteAsync(baseKey).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired keyvalue. key={Key}", baseKey); }
        }

        // 2) Hashsets TTL: TTL dans hashset ttlKey (= baseKey+suffix) champ __ttl__
        foreach (var hs in hashsets)
        {
            ct.ThrowIfCancellationRequested();

            var ttlKey = hs.Key;
            if (!ttlKey.EndsWith(TimeToLiveSuffix, StringComparison.Ordinal))
                continue;

            if (!hs.Value.TryGetValue(HashsetTtlField, out var ttlBytes))
                continue;

            if (!TryReadInt64(ttlBytes, out var expireAtTicks))
                continue;

            if (expireAtTicks > nowTicks)
                continue;

            var baseKey = ttlKey[..^TimeToLiveSuffix.Length];

            try { await _db.HashSetDeleteAsync(baseKey).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired hashset. key={Key}", baseKey); }

            // sécurité : supprimer aussi ttlKey
            try { await _db.HashSetDeleteAsync(ttlKey).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete expired hashset ttlKey. key={Key}", ttlKey); }
        }
    }

    private static bool TryReadInt64(ReadOnlyMemory<byte> bytes, out long value)
    {
        value = 0;
        if (bytes.Length < sizeof(long)) return false;
        value = BitConverter.ToInt64(bytes.Span);
        return true;
    }

    private static bool TryParseFileMetaKey(string key, out string elementId)
    {
        elementId = "";
        if (!key.StartsWith(FileMetaPrefix, StringComparison.Ordinal)) return false;
        if (!key.EndsWith(FileMetaSuffix, StringComparison.Ordinal)) return false;

        var middle = key.Substring(FileMetaPrefix.Length, key.Length - FileMetaPrefix.Length - FileMetaSuffix.Length);
        if (string.IsNullOrWhiteSpace(middle)) return false;

        elementId = middle;
        return true;
    }
}
