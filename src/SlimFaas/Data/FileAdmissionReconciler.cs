using System.Collections.Immutable;
using System.Diagnostics;
using DotNext;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimData.Options;

namespace SlimFaas;

public sealed class FileAdmissionReconciler(IDatabaseService database, ISupplier<SlimDataPayload> state,
    IClusterFileSync sync, IFileRepository repository, IOptions<ClusterFileOptions> options,
    ILogger<FileAdmissionReconciler> logger) : BackgroundService
{
    private static readonly IComparer<string> ReverseOrdinal = Comparer<string>.Create(
        static (left, right) => string.CompareOrdinal(right, left));
    private string? _afterId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.ReconcileIntervalSeconds));
        try
        {
            do
            {
                try { await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogFileReferenceReconciliationFailed(ex); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> ReconcileOnceAsync(CancellationToken ct)
    {
        if (!HasReferences()) { _afterId = null; return 0; }
        _ = await database.GetAsync("\0files:reference-read-barrier").WaitAsync(ct).ConfigureAwait(false);
        string[] batch = SelectBatch();
        if (batch.Length == 0) { _afterId = null; return 0; }
        long started = Stopwatch.GetTimestamp();
        int processed = 0;
        long bytes = 0;
        foreach (string key in batch)
        {
            if (bytes >= options.Value.ReconcileMaxBytes || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5)) break;
            ct.ThrowIfCancellationRequested();
            _afterId = key;
            processed++;
            if (IsMetadataKey(key))
            {
                using var legacyDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                legacyDeadline.CancelAfter(TimeSpan.FromSeconds(options.Value.QueueWaitTimeoutSeconds));
                try
                {
                    var legacy = LegacyReference(key);
                    if (legacy is null) continue;
                    bytes = AddBytes(bytes, legacy.Value.Metadata.Length);
                    await RepairAsync(legacy.Value.Id, legacy.Value.Metadata.Sha256Hex, legacy.Value.Metadata.Length,
                        legacyDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogReferencedFileRepairFailed(ex, key); }
                continue;
            }
            string id = key;
            FileAdmissionEntry? entry = FileAdmissionStore.Find(state.Invoke(), id);
            if (entry is null) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(options.Value.QueueWaitTimeoutSeconds));
            try
            {
                long now = DateTime.UtcNow.Ticks;
                if (entry.Phase == FileAdmissionPhase.Pending && entry.DeadlineUtcTicks > now) continue;
                if (entry.Phase == FileAdmissionPhase.Retired || !FileAdmissionStore.IsReferenced(state.Invoke(), entry, now))
                {
                    FileAdmissionResult retired = await database.ExecuteFileAdmissionAsync(new()
                    {
                        Action = FileAdmissionAction.Retire, FileId = id, Token = entry.Token,
                        DeadlineUtcTicks = entry.DeadlineUtcTicks
                    }).WaitAsync(deadline.Token).ConfigureAwait(false);
                    if (!retired.Applied || retired.Phase != FileAdmissionPhase.Retired) continue;
                    await sync.DeleteLocalAsync(id, deadline.Token).ConfigureAwait(false);
                    if (entry.DeadlineUtcTicks <= now)
                        _ = await database.ExecuteFileAdmissionAsync(new()
                        {
                            Action = FileAdmissionAction.Forget, FileId = id, Token = entry.Token
                        }).WaitAsync(deadline.Token).ConfigureAwait(false);
                    continue;
                }
                if (entry.Phase != FileAdmissionPhase.Active) continue;
                // A single file can exceed the per-tick budget; always allow
                // progress, then stop the tick. The transfer's existing byte
                // and pending limits remain authoritative.
                bytes = AddBytes(bytes, entry.Length);
                await RepairAsync(id, entry.Sha256Hex, entry.Length, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogReferencedFileRepairFailed(ex, id); }
        }
        return processed;
    }

    private ImmutableDictionary<string, ReadOnlyMemory<byte>> CurrentIndex() =>
        state.Invoke().Hashsets.GetValueOrDefault(FileAdmissionStore.IndexKey) ??
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;

    private bool HasReferences()
    {
        if (!CurrentIndex().IsEmpty) return true;
        foreach (string key in state.Invoke().KeyValues.Keys)
            if (IsMetadataKey(key)) return true;
        return false;
    }

    private long AddBytes(long bytes, long length) => length >= options.Value.ReconcileMaxBytes - bytes
        ? options.Value.ReconcileMaxBytes : bytes + length;

    private async Task RepairAsync(string id, string sha256Hex, long length, CancellationToken ct)
    {
        bool available = await repository.ExistsAsync(id, sha256Hex, ct).ConfigureAwait(false);
        if (!available)
        {
            FilePullResult pulled = await sync.PullFileIfMissingAsync(id, sha256Hex, preferredNode: null, ct).ConfigureAwait(false);
            if (pulled.Stream is null) { logger.LogReferencedFileUnavailable(id); return; }
            await pulled.Stream.DisposeAsync().ConfigureAwait(false);
        }
        if (repository is not IDurableFileRepository durable)
            throw new IOException("The repaired file repository cannot confirm persistence.");
        FilePersistenceReceipt receipt = await durable.PersistVersionAsync(id, sha256Hex, length, ct).ConfigureAwait(false);
        if (receipt.Id != id || receipt.Length != length || !receipt.Sha256Hex.Equals(sha256Hex, StringComparison.OrdinalIgnoreCase) ||
            receipt.StorageIdentity != durable.StorageIdentity)
            throw new IOException("The repaired file persistence receipt is invalid.");
        if (!available) logger.LogReferencedFileRepaired(id, length);
    }

    private (string Id, DataSetMetadata Metadata)? LegacyReference(string key)
    {
        var payload = state.Invoke();
        if (!payload.KeyValues.TryGetValue(key, out var bytes)) return null;
        if (payload.KeyValues.TryGetValue(SlimDataInterpreter.TtlKey(key), out var ttl))
        {
            if (ttl.Length != sizeof(long) || BitConverter.ToInt64(ttl.Span) <= DateTime.UtcNow.Ticks) return null;
        }
        var metadata = MemoryPackSerializer.Deserialize<DataSetMetadata>(bytes.Span);
        if (metadata is null || FileAdmissionTags.IsManaged(metadata.Tags)) return null;
        string id = key.Substring(DataFileKeys.MetaPrefix.Length, key.Length - DataFileKeys.MetaPrefix.Length - DataFileKeys.MetaSuffix.Length);
        if (FileAdmissionStore.Find(payload, id) is not null) return null;
        if (metadata.Tags?.TryGetValue("QueueElementId", out string? owner) == true && !string.IsNullOrEmpty(owner))
        {
            foreach (var queue in payload.Queues.Values)
            foreach (var item in queue)
                if (item.Id == owner) return (id, metadata);
            foreach (string root in DeadLetterKeys.GetPayloadOwnerIds(payload))
                if (root == owner) return (id, metadata);
            return null;
        }
        return DataFileKeys.IsInternalElementId(id) ? null : (id, metadata);
    }

    private static bool IsMetadataKey(string key) => key.StartsWith(DataFileKeys.MetaPrefix, StringComparison.Ordinal) &&
        key.EndsWith(DataFileKeys.MetaSuffix, StringComparison.Ordinal);

    private string[] SelectBatch()
    {
        // Only a bounded window of IDs survives this synchronous scan. Neither
        // old index versions nor payload snapshots are held across transfers.
        int limit = options.Value.ReconcileBatchSize;
        var selected = new PriorityQueue<string, string>(limit, ReverseOrdinal);
        foreach (string id in CurrentIndex().Keys)
        {
            AddCandidate(id);
        }
        foreach (string key in state.Invoke().KeyValues.Keys)
            if (IsMetadataKey(key)) AddCandidate(key);
        var batch = new string[selected.Count];
        for (int index = batch.Length - 1; index >= 0; index--) batch[index] = selected.Dequeue();
        return batch;

        void AddCandidate(string id)
        {
            if (_afterId is not null && string.CompareOrdinal(id, _afterId) <= 0) return;
            if (selected.Count < limit) selected.Enqueue(id, id);
            else if (string.CompareOrdinal(id, selected.Peek()) < 0) selected.DequeueEnqueue(id, id);
        }
    }
}
