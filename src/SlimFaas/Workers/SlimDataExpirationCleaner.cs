using System.Collections.Immutable;
using DotNext;
using MemoryPack;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas;

namespace SlimData.Expiration;

public sealed class SlimDataExpirationCleaner
{
    private const string OffloadMetaPrefix = "data:file:";
    private const string OffloadMetaSuffix = ":meta";
    private const string QueueElementIdTagKey = "QueueElementId";

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
        var queues    = payload.Queues ?? ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty;

        var nowTicks = DateTime.UtcNow.Ticks;

        // Build set of active QueueElementIds for orphan detection (once per run)
        var activeQueueIds = BuildActiveQueueIds(queues);

        // 1) KeyValues TTL: keys ending with the TTL postfix
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
            _logger.LogDebug("Deleting expired keyvalue. key={Key}", baseKey);

            try
            {
                await _db.DeleteAsync(baseKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired keyvalue. key={Key}", baseKey);
            }
        }

        // 2) Hashsets TTL
        foreach (var hs in hashsets)
        {
            ct.ThrowIfCancellationRequested();

            var key = hs.Key;

            long? expireAtTicks = TryReadHashsetExpireAt(hs.Value);

            if (expireAtTicks is null)
                continue;

            if (expireAtTicks.Value > nowTicks)
                continue;

            try
            {
                await _db.HashSetDeleteAsync(key, "").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired hashset. key={Key}", key);
            }
        }

        // 3) Local disk: TTL expiration + orphaned offload files (single pass)
        await foreach (var entry in _files.EnumerateAllMetadataAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            // TTL expiration takes precedence
            var exp = entry.Metadata.ExpireAtUtcTicks;
            if (exp is long t && t > 0 && t <= nowTicks)
            {
                _logger.LogDebug("Deleting expired local file by disk metadata. id={Id} expireAt={ExpireAt}", entry.Id, t);
                try
                {
                    await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete expired local file. id={Id}", entry.Id);
                }
                continue;
            }

            // Orphaned offload file: has a QueueElementId tag but the queue element no longer exists
            if (entry.Metadata.Tags is not null &&
                entry.Metadata.Tags.TryGetValue(QueueElementIdTagKey, out var fileQueueElementId) &&
                !string.IsNullOrEmpty(fileQueueElementId) &&
                !activeQueueIds.Contains(fileQueueElementId))
            {
                _logger.LogDebug("Deleting orphaned offload file. id={Id} QueueElementId={QueueElementId}", entry.Id, fileQueueElementId);
                try
                {
                    await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete orphaned offload file. id={Id}", entry.Id);
                }
            }
        }

        // 4) RAFT offload metadata keys: delete when the linked QueueElementId is no longer active
        foreach (var kv in keyValues)
        {
            ct.ThrowIfCancellationRequested();

            var key = kv.Key;
            if (!key.StartsWith(OffloadMetaPrefix, StringComparison.Ordinal) ||
                !key.EndsWith(OffloadMetaSuffix, StringComparison.Ordinal))
                continue;

            DataSetMetadata? meta;
            try
            {
                meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(kv.Value.Span);
            }
            catch
            {
                continue; // corrupted or wrong format – skip silently
            }

            if (meta?.Tags is null)
                continue;

            if (!meta.Tags.TryGetValue(QueueElementIdTagKey, out var queueElementId) ||
                string.IsNullOrEmpty(queueElementId))
                continue;

            if (activeQueueIds.Contains(queueElementId))
                continue;

            _logger.LogDebug("Deleting orphaned offload metadata. key={Key} QueueElementId={QueueElementId}", key, queueElementId);
            try
            {
                await _db.DeleteAsync(key).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete orphaned offload metadata. key={Key}", key);
            }
        }

        // 5) Cleanup orphan .tmp files (interrupted uploads)
        try
        {
            var deleted = await _files.CleanupOrphanTempFilesAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogInformation("Cleaned up {Count} orphan .tmp file(s) from disk.", deleted);
            else
                _logger.LogDebug("No orphan .tmp files found during cleanup.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup orphan .tmp files.");
        }
    }

    internal static HashSet<string> BuildActiveQueueIds(
        ImmutableDictionary<string, ImmutableArray<QueueElement>> queues)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var queue in queues.Values)
            foreach (var elem in queue)
                ids.Add(elem.Id);
        return ids;
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
