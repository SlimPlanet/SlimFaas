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
        activeQueueIds.UnionWith(DeadLetterKeys.GetPayloadOwnerIds(payload));
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
            if (keyValues.TryGetValue(baseKey, out var expiringMetadata) && IsManagedFileMetadata(baseKey, expiringMetadata))
                continue;
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

        // 3) Immutable body revisions follow durable metadata, queue and DLQ
        // references. A local current-file hint cannot retain an obsolete revision
        // or justify deleting a different revision.
        await foreach (var entry in _files.EnumerateAllMetadataAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (FileAdmissionTags.IsManaged(entry.Metadata.Tags) || FileAdmissionStore.Find(payload, entry.Id) is not null)
            {
                try
                {
                    if (await CanDeleteAdmittedFileAsync(entry, payload, nowTicks).ConfigureAwait(false))
                        await _files.DeleteVersionAsync(entry.Id, entry.Metadata.Sha256Hex, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogFailedToDeleteLocalFileWithout(ex, entry.Id);
                }
                continue;
            }
            string candidateKey = DiskCandidatePrefix + entry.Id + ":" + entry.Metadata.Sha256Hex;
            bool queueIsActive = entry.Metadata.Tags is not null &&
                entry.Metadata.Tags.TryGetValue(QueueElementIdTagKey, out string? queueId) &&
                activeQueueIds.Contains(queueId);
            if (queueIsActive || IsReferencedVersion(entry, keyValues, activeQueueIds, nowTicks))
            {
                _orphanCandidates.Remove(candidateKey);
                continue;
            }

            observedOrphanArtifacts.Add(candidateKey);
            bool expired = entry.Metadata.ExpireAtUtcTicks is long deadline && deadline > 0 && deadline <= nowTicks;
            if (!expired && !IsConfirmedOrphan(candidateKey))
                continue;
            try
            {
                await _files.DeleteVersionAsync(entry.Id, entry.Metadata.Sha256Hex, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogFailedToDeleteLocalFileWithout(ex, entry.Id);
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

            if (meta?.Tags is null || FileAdmissionTags.IsManaged(meta.Tags))
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

    private async Task<bool> CanDeleteAdmittedFileAsync(FileMetadataEntry file, SlimDataPayload payload, long nowTicks)
    {
        var admission = FileAdmissionStore.Find(payload, file.Id);
        string token;
        long deadline;
        if (file.Metadata.Tags is not null && FileAdmissionTags.IsManaged(file.Metadata.Tags))
        {
            if (!FileAdmissionTags.TryRead(file.Metadata.Tags, out token, out deadline))
            {
                _logger.LogInvalidFileAdmissionMarker(file.Id);
                return false;
            }
        }
        else if (admission is not null)
        {
            token = admission.Token;
            deadline = admission.DeadlineUtcTicks;
        }
        else
        {
            _logger.LogInvalidFileAdmissionMarker(file.Id);
            return false;
        }
        if (admission is not null)
        {
            if (admission.Token != token) return false;
            if (admission.Phase == FileAdmissionPhase.Retired) return true;
            if (FileAdmissionStore.IsReferenced(payload, admission, nowTicks)) return false;
        }
        else if (deadline > nowTicks) return false;

        var result = await _db.ExecuteFileAdmissionAsync(new()
        {
            Action = FileAdmissionAction.Retire, FileId = file.Id, Token = token, DeadlineUtcTicks = deadline
        }).ConfigureAwait(false);
        return result.Applied && result.Phase == FileAdmissionPhase.Retired;
    }

    private static bool IsManagedFileMetadata(string key, ReadOnlyMemory<byte> value)
    {
        if (!key.StartsWith(DataFileKeys.MetaPrefix, StringComparison.Ordinal) ||
            !key.EndsWith(DataFileKeys.MetaSuffix, StringComparison.Ordinal)) return false;
        try
        {
            return FileAdmissionTags.IsManaged(MemoryPackSerializer.Deserialize<DataSetMetadata>(value.Span)?.Tags);
        }
        catch (MemoryPackSerializationException) { return false; }
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

    private static bool IsReferencedVersion(
        FileMetadataEntry entry,
        ImmutableDictionary<string, ReadOnlyMemory<byte>> keyValues,
        HashSet<string> activeQueueIds,
        long nowTicks)
    {
        string key = DataFileKeys.MetaKey(entry.Id);
        if (!keyValues.TryGetValue(key, out ReadOnlyMemory<byte> bytes))
            return false;
        if (keyValues.TryGetValue(SlimDataInterpreter.TtlKey(key), out ReadOnlyMemory<byte> ttl) &&
            TryReadInt64(ttl, out long expiration) && expiration > 0 && expiration <= nowTicks)
            return false;
        DataSetMetadata? metadata;
        try
        {
            metadata = MemoryPackSerializer.Deserialize<DataSetMetadata>(bytes.Span);
        }
        catch
        {
            // An unreadable durable reference cannot prove a revision disposable.
            // The separate metadata audit logs the malformed record.
            return true;
        }
        if (metadata is null || string.IsNullOrEmpty(metadata.Sha256Hex))
            return true;
        if (!metadata.Sha256Hex.Equals(entry.Metadata.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            return false;
        return metadata.Tags is null ||
               !metadata.Tags.TryGetValue(QueueElementIdTagKey, out string? queueId) ||
               string.IsNullOrEmpty(queueId) || activeQueueIds.Contains(queueId);
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
