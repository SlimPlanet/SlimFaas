using System.Collections.Immutable;
using DotNext;
using MemoryPack;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas;

namespace SlimData.Expiration;

public sealed class SlimDataExpirationCleaner
{
    private const string QueueElementIdTagKey = "QueueElementId";
    internal const int DefaultOrphanConfirmationCycles = 3;
    private const string DiskCandidatePrefix = "disk:";
    private const string MetadataCandidatePrefix = "metadata:";

    private readonly ISupplier<SlimDataPayload> _state;
    private readonly IDatabaseService _db;
    private readonly IFileRepository _files;
    private readonly ILogger<SlimDataExpirationCleaner> _logger;
    private readonly int _orphanConfirmationCycles;
    private readonly Dictionary<string, int> _orphanCandidates = new(StringComparer.Ordinal);

    public SlimDataExpirationCleaner(
        ISupplier<SlimDataPayload> state,
        IDatabaseService db,
        IFileRepository files,
        ILogger<SlimDataExpirationCleaner> logger,
        int orphanConfirmationCycles = DefaultOrphanConfirmationCycles)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _orphanConfirmationCycles = orphanConfirmationCycles > 0
            ? orphanConfirmationCycles
            : throw new ArgumentOutOfRangeException(
                nameof(orphanConfirmationCycles),
                orphanConfirmationCycles,
                "The number of orphan confirmation cycles must be strictly positive.");
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
        var observedOrphanArtifacts = new HashSet<string>(StringComparer.Ordinal);

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
                _logger.LogDebug("Deleting expired keyvalue. key={Key}", key);
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

            string? fileQueueElementId = null;
            var hasQueueElementTag =
                entry.Metadata.Tags is not null &&
                entry.Metadata.Tags.TryGetValue(QueueElementIdTagKey, out fileQueueElementId) &&
                !string.IsNullOrEmpty(fileQueueElementId);
            var queueElementIsActive =
                hasQueueElementTag && activeQueueIds.Contains(fileQueueElementId!);
            var candidateKey = DiskCandidatePrefix + entry.Id;

            // A file can be written just before its Raft metadata is committed. Requiring
            // several consecutive observations protects that window and cluster convergence.
            if (!keyValues.ContainsKey(DataFileKeys.MetaKey(entry.Id)))
            {
                observedOrphanArtifacts.Add(candidateKey);
                if (queueElementIsActive)
                {
                    _orphanCandidates.Remove(candidateKey);
                    continue;
                }

                if (!IsConfirmedOrphan(candidateKey))
                    continue;

                _logger.LogDebug(
                    "Deleting confirmed local file without Raft metadata. id={Id}",
                    entry.Id);
                try
                {
                    await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete local file without Raft metadata. id={Id}", entry.Id);
                }
                continue;
            }

            // Orphaned offload file: its metadata still exists, but the queue item is gone.
            if (hasQueueElementTag)
            {
                observedOrphanArtifacts.Add(candidateKey);
                if (queueElementIsActive)
                {
                    _orphanCandidates.Remove(candidateKey);
                    continue;
                }

                if (!IsConfirmedOrphan(candidateKey))
                    continue;

                _logger.LogDebug(
                    "Deleting confirmed orphaned offload file. id={Id} QueueElementId={QueueElementId}",
                    entry.Id,
                    fileQueueElementId);
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
            if (!key.StartsWith(DataFileKeys.MetaPrefix, StringComparison.Ordinal) ||
                !key.EndsWith(DataFileKeys.MetaSuffix, StringComparison.Ordinal))
                continue;

            DataSetMetadata? meta;
            try
            {
                meta = MemoryPackSerializer.Deserialize<DataSetMetadata>(kv.Value.Span);
            }
            catch
            {
                _logger.LogWarning("Failed to read metadata. key={Key} Value={Value}", key, kv.Value);
                continue;
            }

            if (meta?.Tags is null)
                continue;

            if (!meta.Tags.TryGetValue(QueueElementIdTagKey, out var queueElementId) ||
                string.IsNullOrEmpty(queueElementId))
                continue;

            var candidateKey = MetadataCandidatePrefix + key;
            observedOrphanArtifacts.Add(candidateKey);
            if (activeQueueIds.Contains(queueElementId))
            {
                _orphanCandidates.Remove(candidateKey);
                continue;
            }

            if (!IsConfirmedOrphan(candidateKey))
                continue;

            _logger.LogDebug(
                "Deleting confirmed orphaned offload metadata. key={Key} QueueElementId={QueueElementId}",
                key,
                queueElementId);
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

        foreach (var candidateKey in _orphanCandidates.Keys.ToArray())
        {
            if (!observedOrphanArtifacts.Contains(candidateKey))
                _orphanCandidates.Remove(candidateKey);
        }
    }

    private bool IsConfirmedOrphan(string candidateKey)
    {
        _orphanCandidates.TryGetValue(candidateKey, out var observedCycles);
        observedCycles = Math.Min(observedCycles + 1, _orphanConfirmationCycles);
        _orphanCandidates[candidateKey] = observedCycles;
        return observedCycles >= _orphanConfirmationCycles;
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
