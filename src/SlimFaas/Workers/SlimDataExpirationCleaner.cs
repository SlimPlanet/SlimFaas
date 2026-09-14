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
    private const int MaximumMetadataDeleteBatchSize = 64;

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
            _logger.LogDeletingExpiredKeyvalueKey(baseKey);

            try
            {
                await _db.DeleteAsync(baseKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogFailedToDeleteExpiredKeyvalueKey(ex, baseKey);
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
                _logger.LogDeletingExpiredKeyvalueKey2(key);
                await _db.HashSetDeleteAsync(key, "").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogFailedToDeleteExpiredHashsetKey(ex, key);
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
                _logger.LogDeletingExpiredLocalFileByDisk(entry.Id, t);
                try
                {
                    await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogFailedToDeleteExpiredLocalFile(ex, entry.Id);
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

                _logger.LogDeletingConfirmedLocalFileWithoutRaft(entry.Id);
                try
                {
                    await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogFailedToDeleteLocalFileWithout(ex, entry.Id);
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

                _logger.LogDeletingConfirmedOrphanedOffloadFileId(entry.Id, fileQueueElementId);
                try
                {
                    await _files.DeleteAsync(entry.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogFailedToDeleteOrphanedOffloadFile(ex, entry.Id);
                }
            }
        }

        // 4) RAFT offload metadata keys: delete when the linked QueueElementId is no longer active
        var confirmedOrphanMetadata = new List<string>();
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
                _logger.LogFailedToReadMetadataKeyValue(key, kv.Value);
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

            _logger.LogDeletingConfirmedOrphanedOffloadMetadataKey(key, queueElementId);
            confirmedOrphanMetadata.Add(key);
        }
        await DeleteOrphanMetadataAsync(confirmedOrphanMetadata).ConfigureAwait(false);

        // 5) Cleanup orphan .tmp files (interrupted uploads)
        try
        {
            var deleted = await _files.CleanupOrphanTempFilesAsync(ct).ConfigureAwait(false);
            if (deleted > 0)
                _logger.LogCleanedUpOrphanTmpFileFrom(deleted);
            else
                _logger.LogNoOrphanTmpFilesFoundDuring();
        }
        catch (Exception ex)
        {
            _logger.LogFailedToCleanupOrphanTmpFiles(ex);
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

    private async Task DeleteOrphanMetadataAsync(IReadOnlyCollection<string> keys)
    {
        foreach (string[] batch in keys.Chunk(MaximumMetadataDeleteBatchSize))
            await Task.WhenAll(batch.Select(DeleteOneOrphanMetadataAsync)).ConfigureAwait(false);
    }

    private async Task DeleteOneOrphanMetadataAsync(string key)
    {
        try
        {
            await _db.DeleteAsync(key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogFailedToDeleteOrphanedOffloadMetadata(ex, key);
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
