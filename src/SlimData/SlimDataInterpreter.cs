using System.Collections.Immutable;
using System.Diagnostics;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;

namespace SlimData;

public class SlimDataState(
    ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> Hashsets,
    ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues,
    ImmutableDictionary<string, ImmutableArray<QueueElement>> Queues)
{
    public ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> Hashsets { get; set; } = Hashsets;
    public ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues { get; set; } = KeyValues;
    public ImmutableDictionary<string, ImmutableArray<QueueElement>> Queues { get; set; } = Queues;
}
public sealed class QueueElement
{
    public QueueElement(
        ReadOnlyMemory<byte> value,
        string id,
        long insertTimeStamp,
        int httpTimeoutSeconds,
        ImmutableArray<int> timeoutRetriesSeconds,                // <— ImmutableArray
        ImmutableArray<QueueHttpTryElement> retryQueueElements,   // <— ImmutableArray
        ImmutableHashSet<int> httpStatusRetries                   // <— HashSet immuable
    )
    {
        Value = value;
        Id = id;
        InsertTimeStamp = insertTimeStamp;
        HttpTimeoutSeconds = httpTimeoutSeconds;
        TimeoutRetriesSeconds = timeoutRetriesSeconds;
        RetryQueueElements = retryQueueElements;
        HttpStatusRetries = httpStatusRetries;
        HttpTimeoutTicks = (long)httpTimeoutSeconds * TimeSpan.TicksPerSecond; // pré-calcul
    }

    public ReadOnlyMemory<byte> Value { get; }
    public string Id { get; }
    public long InsertTimeStamp { get; }
    public int HttpTimeoutSeconds { get; }
    public long HttpTimeoutTicks { get; } // <— évite TimeSpan.FromSeconds dans les hot-paths

    public ImmutableArray<int> TimeoutRetriesSeconds { get; }
    public ImmutableArray<QueueHttpTryElement> RetryQueueElements { get; set; } // reste remplaçable atomiquement
    public ImmutableHashSet<int> HttpStatusRetries { get; }
}

public sealed class QueueHttpTryElement
{
    public QueueHttpTryElement(long startTimeStamp = 0, string idTransaction = "", long endTimeStamp = 0, int httpCode = 0)
    {
        StartTimeStamp = startTimeStamp;
        IdTransaction = idTransaction;
        EndTimeStamp = endTimeStamp;
        HttpCode = httpCode;
    }

    public long StartTimeStamp { get; set; }
    public long EndTimeStamp { get; set; }
    public int HttpCode { get; set; }
    public string IdTransaction { get; set; }
}

#pragma warning restore CA2252using System;


public class SlimDataInterpreter : CommandInterpreter
{
    public const int DeleteFromQueueCode = 1000;

    public SlimDataState SlimDataState = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty // <— queues en ImmutableArray
    );

    [CommandHandler]
    public ValueTask ListRightPopAsync(ListRightPopCommand addHashSetCommand, CancellationToken token)
        => DoListRightPopAsync(addHashSetCommand, SlimDataState);

    internal static ValueTask DoListRightPopAsync(ListRightPopCommand listRightPopCommand, SlimDataState slimDataState)
    {
        var queues = slimDataState.Queues;

        if (queues.TryGetValue(listRightPopCommand.Key, out var queue))
        {
            var nowTicks = listRightPopCommand.NowTicks;

            // 1) Marquer timeouts (modif en place du dernier try des éléments concernés)
            var queueTimeoutElements = queue.GetQueueTimeoutElement(nowTicks);
            for (int i = 0; i < queueTimeoutElements.Length; i++)
            {
                var qe = queueTimeoutElements[i];
                if (qe.RetryQueueElements.IsDefaultOrEmpty) continue;
                var last = qe.RetryQueueElements[^1];
                last.EndTimeStamp = nowTicks;
                last.HttpCode = 504;
            }

            // 2) Retirer les éléments terminés en un seul passage
            var finished = queue.GetQueueFinishedElement(nowTicks);
            if (!finished.IsDefaultOrEmpty)
            {
                var keep = ImmutableArray.CreateBuilder<QueueElement>(queue.Length - finished.Length);
                for (int i = 0; i < queue.Length; i++)
                {
                    var e = queue[i];
                    // binaire « est-il fini ? »
                    bool isFinished = false;
                    // NB: Inutile d’assembler un HashSet pour finished (souvent faible).
                    for (int j = 0; j < finished.Length; j++)
                    {
                        if (ReferenceEquals(e, finished[j])) { isFinished = true; break; }
                    }
                    if (!isFinished) keep.Add(e);
                }
                queue = keep.MoveToImmutable();
            }

            // 3) Si pas déjà pris par la même transaction, sélectionner N éléments disponibles et démarrer un try
            bool idTxAlreadyExists = false;
            if (!queue.IsDefaultOrEmpty)
            {
                for (int i = 0; i < queue.Length; i++)
                {
                    var e = queue[i];
                    if (!e.RetryQueueElements.IsDefaultOrEmpty &&
                        e.RetryQueueElements[^1].IdTransaction == listRightPopCommand.IdTransaction)
                    {
                        idTxAlreadyExists = true;
                        break;
                    }
                }
            }

            if (!idTxAlreadyExists)
            {
                var available = queue.GetQueueAvailableElement(nowTicks, listRightPopCommand.Count);
                for (int i = 0; i < available.Length; i++)
                {
                    var e = available[i];
                    var b = e.RetryQueueElements.IsDefault ? ImmutableArray.CreateBuilder<QueueHttpTryElement>() : e.RetryQueueElements.ToBuilder();
                    b.Add(new QueueHttpTryElement(listRightPopCommand.NowTicks, listRightPopCommand.IdTransaction));
                    e.RetryQueueElements = b.ToImmutable();
                }
            }

            queues = queues.SetItem(listRightPopCommand.Key, queue);
        }

        slimDataState.Queues = queues;
        return default;
    }

    [CommandHandler]
    public ValueTask ListLeftPushBatchAsync(ListLeftPushBatchCommand listLeftPushBatchCommand, CancellationToken token)
        => DoListLeftPushBatchAsync(listLeftPushBatchCommand, SlimDataState);

    internal static ValueTask DoListLeftPushBatchAsync(ListLeftPushBatchCommand cmd, SlimDataState state)
    {
        var queues = state.Queues;
        if (cmd.Items is null || cmd.Items.Count == 0)
            return default;

        foreach (var item in cmd.Items)
        {
            var qe = new QueueElement(
                item.Value,
                item.Identifier,
                item.NowTicks,
                item.RetryTimeout,
                item.Retries is null ? ImmutableArray<int>.Empty : item.Retries.ToImmutableArray(),
                ImmutableArray<QueueHttpTryElement>.Empty,
                item.HttpStatusCodesWorthRetrying is null ? ImmutableHashSet<int>.Empty : item.HttpStatusCodesWorthRetrying.ToImmutableHashSet()
            );

            if (queues.TryGetValue(item.Key, out var arr))
            {
                // Unicité par Id
                bool exists = false;
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i].Id == item.Identifier) { exists = true; break; }
                }
                if (!exists)
                {
                    var b = arr.ToBuilder();
                    b.Add(qe);
                    queues = queues.SetItem(item.Key, b.ToImmutable());
                }
            }
            else
            {
                queues = queues.Add(item.Key, ImmutableArray.Create(qe));
            }
        }

        state.Queues = queues;
        return default;
    }

    [CommandHandler]
    public ValueTask ListLeftPushAsync(ListLeftPushCommand listLeftPushCommand, CancellationToken token)
        => DoListLeftPushAsync(listLeftPushCommand, SlimDataState);

    internal static ValueTask DoListLeftPushAsync(ListLeftPushCommand cmd, SlimDataState state)
    {
        var queues = state.Queues;

        var qe = new QueueElement(
            cmd.Value,
            cmd.Identifier,
            cmd.NowTicks,
            cmd.RetryTimeout,
            cmd.Retries is null ? ImmutableArray<int>.Empty : cmd.Retries.ToImmutableArray(),
            ImmutableArray<QueueHttpTryElement>.Empty,
            cmd.HttpStatusCodesWorthRetrying is null ? ImmutableHashSet<int>.Empty : cmd.HttpStatusCodesWorthRetrying.ToImmutableHashSet()
        );

        if (queues.TryGetValue(cmd.Key, out var arr))
        {
            bool exists = false;
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i].Id == cmd.Identifier) { exists = true; break; }
            }
            if (!exists)
            {
                var b = arr.ToBuilder();
                b.Add(qe);
                queues = queues.SetItem(cmd.Key, b.ToImmutable());
            }
        }
        else
        {
            queues = queues.Add(cmd.Key, ImmutableArray.Create(qe));
        }

        state.Queues = queues;

        // ---- Logs (sans LINQ) ----
        int totalQueueElements = 0;
        long totalQueueBytes = 0;
        foreach (var kv in state.Queues)
        {
            var arr1 = kv.Value;
            int count = arr.Length;
            totalQueueElements += count;

            long sizeBytes = 0;
            for (int i = 0; i < arr1.Length; i++) sizeBytes += arr1[i].Value.Length;
            totalQueueBytes += sizeBytes;

            Console.WriteLine($"[Queue] Key: {kv.Key}, Count: {count}, Size: {sizeBytes / (1024.0 * 1024.0):F2} MB");
        }
        Console.WriteLine($"[Queues] Total Elements: {totalQueueElements}, Total Size: {totalQueueBytes / (1024.0 * 1024.0):F2} MB");

        return default;
    }

    [CommandHandler]
    public ValueTask ListCallbackAsync(ListCallbackCommand addHashSetCommand, CancellationToken token)
        => DoListCallbackAsync(addHashSetCommand, SlimDataState);

    internal static ValueTask DoListCallbackAsync(ListCallbackCommand cmd, SlimDataState state)
    {
        var queues = state.Queues;
        if (!queues.TryGetValue(cmd.Key, out var arr))
            return default;

        var work = arr; // point de départ

        // On construit une nouvelle file si des suppressions se produisent
        for (int i = 0; i < cmd.CallbackElements.Count; i++)
        {
            var cb = cmd.CallbackElements[i];

            // Recherche par Id (lineaire)
            int idx = -1;
            for (int j = 0; j < work.Length; j++)
            {
                if (work[j].Id == cb.Identifier) { idx = j; break; }
            }
            if (idx < 0) continue;

            var qe = work[idx];
            if (cb.HttpCode == DeleteFromQueueCode)
            {
                // suppression
                var b = work.ToBuilder();
                b.RemoveAt(idx);
                work = b.ToImmutable();
                continue;
            }

            if (!qe.RetryQueueElements.IsDefaultOrEmpty)
            {
                var last = qe.RetryQueueElements[^1];
                last.EndTimeStamp = cmd.NowTicks;
                last.HttpCode = cb.HttpCode;

                if (qe.IsFinished(cmd.NowTicks))
                {
                    var b = work.ToBuilder();
                    b.RemoveAt(idx);
                    work = b.ToImmutable();
                }
            }
        }

        queues = queues.SetItem(cmd.Key, work);
        state.Queues = queues;

        // Logs (sans LINQ)
        int totalQueueElements = 0;
        long totalQueueBytes = 0;
        foreach (var kv in state.Queues)
        {
            var a = kv.Value;
            int count = a.Length;
            totalQueueElements += count;

            long sizeBytes = 0;
            for (int i = 0; i < a.Length; i++) sizeBytes += a[i].Value.Length;
            totalQueueBytes += sizeBytes;

            Console.WriteLine($"[Queue] Key: {kv.Key}, Count: {count}, Size: {sizeBytes / (1024.0 * 1024.0):F2} MB");
        }
        Console.WriteLine($"[Queues] Total Elements: {totalQueueElements}, Total Size: {totalQueueBytes / (1024.0 * 1024.0):F2} MB");

        return default;
    }

    [CommandHandler]
    public ValueTask ListCallbackBatchAsync(ListCallbackBatchCommand command, CancellationToken token)
        => DoListCallbackBatchAsync(command, SlimDataState);

    internal static ValueTask DoListCallbackBatchAsync(ListCallbackBatchCommand batch, SlimDataState state)
    {
        var queues = state.Queues;
        if (batch.Items is null || batch.Items.Count == 0)
            return default;

        foreach (var item in batch.Items)
        {
            if (!queues.TryGetValue(item.Key, out var arr) || item.CallbackElements is null || item.CallbackElements.Count == 0)
                continue;

            var work = arr;

            for (int i = 0; i < item.CallbackElements.Count; i++)
            {
                var cb = item.CallbackElements[i];

                // find by id
                int idx = -1;
                for (int j = 0; j < work.Length; j++)
                {
                    if (work[j].Id == cb.Identifier) { idx = j; break; }
                }
                if (idx < 0) continue;

                var qe = work[idx];
                if (cb.HttpCode == DeleteFromQueueCode)
                {
                    var b = work.ToBuilder();
                    b.RemoveAt(idx);
                    work = b.ToImmutable();
                }
                else if (!qe.RetryQueueElements.IsDefaultOrEmpty)
                {
                    var last = qe.RetryQueueElements[^1];
                    last.EndTimeStamp = item.NowTicks;
                    last.HttpCode = cb.HttpCode;

                    if (qe.IsFinished(item.NowTicks))
                    {
                        var b = work.ToBuilder();
                        b.RemoveAt(idx);
                        work = b.ToImmutable();
                    }
                }
            }

            queues = queues.SetItem(item.Key, work);
        }

        state.Queues = queues;
        return default;
    }

    [CommandHandler]
    public ValueTask AddHashSetAsync(AddHashSetCommand addHashSetCommand, CancellationToken token)
        => DoAddHashSetAsync(addHashSetCommand, SlimDataState);

    internal static ValueTask DoAddHashSetAsync(AddHashSetCommand cmd, SlimDataState state)
    {
        var hashsets = state.Hashsets;

        var key = cmd.Key;
        var newPairs = cmd.Value;

        if (hashsets.TryGetValue(key, out var existing))
        {
            var dict = existing.ToBuilder();
            foreach (var kv in newPairs) dict[kv.Key] = kv.Value;
            state.Hashsets = hashsets.SetItem(key, dict.ToImmutable());
        }
        else
        {
            state.Hashsets = hashsets.SetItem(key, newPairs.ToImmutableDictionary());
        }

        return default;
    }

    [CommandHandler]
    public ValueTask AddKeyValueAsync(AddKeyValueCommand valueCommand, CancellationToken token)
        => DoAddKeyValueAsync(valueCommand, SlimDataState);

    internal static ValueTask DoAddKeyValueAsync(AddKeyValueCommand cmd, SlimDataState state)
    {
        var keyValues = state.KeyValues;
        state.KeyValues = keyValues.SetItem(cmd.Key, cmd.Value);
        return default;
    }

    [CommandHandler]
    public ValueTask DeleteKeyValueAsync(DeleteKeyValueCommand valueCommand, CancellationToken token)
        => DoDeleteKeyValueAsync(valueCommand, SlimDataState);

    internal static ValueTask DoDeleteKeyValueAsync(DeleteKeyValueCommand cmd, SlimDataState state)
    {
        var keyValues = state.KeyValues;
        if (keyValues.ContainsKey(cmd.Key))
            state.KeyValues = keyValues.Remove(cmd.Key);
        return default;
    }

    [CommandHandler]
    public ValueTask DeleteHashSetAsync(DeleteHashSetCommand valueCommand, CancellationToken token)
        => DoDeleteHashSetAsync(valueCommand, SlimDataState);

    internal static ValueTask DoDeleteHashSetAsync(DeleteHashSetCommand cmd, SlimDataState state)
    {
        var key = cmd.Key;
        if (string.IsNullOrEmpty(key) || !state.Hashsets.ContainsKey(key))
            return default;

        if (string.IsNullOrEmpty(cmd.DictionaryKey))
        {
            state.Hashsets = state.Hashsets.Remove(key);
        }
        else
        {
            var dict = state.Hashsets[key];
            if (dict.ContainsKey(cmd.DictionaryKey))
                state.Hashsets = state.Hashsets.SetItem(key, dict.Remove(cmd.DictionaryKey));
        }

        return default;
    }

    [CommandHandler]
    public ValueTask HandleSnapshotAsync(LogSnapshotCommand command, CancellationToken token)
    {
        DoHandleSnapshotAsync(command, SlimDataState);
        return default;
    }

    internal static ValueTask DoHandleSnapshotAsync(LogSnapshotCommand command, SlimDataState state)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine("=== Start Snapshot ===");

        // ---- KeyValues ----
        long totalKeyValuesBytes = 0;
        foreach (var kv in command.keysValues) totalKeyValuesBytes += kv.Value.Length;
        Console.WriteLine($"[KeyValues] Count: {command.keysValues.Count}, Total Size: {totalKeyValuesBytes / (1024.0 * 1024.0):F2} MB");

        state.KeyValues = command.keysValues.ToImmutableDictionary();

        // ---- Queues ----
        int totalQueueElements = 0;
        long totalQueueBytes = 0;

        foreach (var q in command.queues)
        {
            var list = q.Value; // supposé IEnumerable<QueueElement> ou List<QueueElement>
            int count = 0;
            long bytes = 0;

            // on prépare l’ImmutableArray
            var builder = ImmutableArray.CreateBuilder<QueueElement>();

            foreach (var e in list)
            {
                builder.Add(e);
                count++;
                bytes += e.Value.Length;
            }

            totalQueueElements += count;
            totalQueueBytes += bytes;

            Console.WriteLine($"[Queue] Key: {q.Key}, Count: {count}, Size: {bytes / (1024.0 * 1024.0):F2} MB");

            // persiste la nouvelle file
            // (si q.Value est déjà un ImmutableArray, remplace par un MoveToImmutable conditionnel)
            var arr = builder.ToImmutable();
            state.Queues = state.Queues.SetItem(q.Key, arr);
        }

        Console.WriteLine($"[Queues] Total Elements: {totalQueueElements}, Total Size: {totalQueueBytes / (1024.0 * 1024.0):F2} MB");

        // ---- Hashsets ----
        int totalHashsetElements = 0;
        long totalHashsetBytes = 0;

        foreach (var hs in command.hashsets)
        {
            int count = 0;
            long bytes = 0;
            foreach (var kv in hs.Value) { count++; bytes += kv.Value.Length; }

            totalHashsetElements += count;
            totalHashsetBytes += bytes;

            Console.WriteLine($"[Hashset] Key: {hs.Key}, Count: {count}, Size: {bytes / (1024.0 * 1024.0):F2} MB");
        }

        Console.WriteLine($"[Hashsets] Total Elements: {totalHashsetElements}, Total Size: {totalHashsetBytes / (1024.0 * 1024.0):F2} MB");

        // persistance hashsets
        var newHashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty;
        foreach (var hs in command.hashsets)
            newHashsets = newHashsets.SetItem(hs.Key, hs.Value.ToImmutableDictionary());
        state.Hashsets = newHashsets;

        sw.Stop();
        Console.WriteLine($"=== End Snapshot (Duration: {sw.ElapsedMilliseconds} ms) ===");
        return default;
    }

    public static CommandInterpreter InitInterpreter(SlimDataState state)
    {
        ValueTask ListRightPopHandler(ListRightPopCommand c, CancellationToken t) => DoListRightPopAsync(c, state);
        ValueTask ListLeftPushHandler(ListLeftPushCommand c, CancellationToken t) => DoListLeftPushAsync(c, state);
        ValueTask ListLeftPushBatchHandler(ListLeftPushBatchCommand c, CancellationToken t) => DoListLeftPushBatchAsync(c, state);
        ValueTask AddHashSetHandler(AddHashSetCommand c, CancellationToken t) => DoAddHashSetAsync(c, state);
        ValueTask DeleteHashSetHandler(DeleteHashSetCommand c, CancellationToken t) => DoDeleteHashSetAsync(c, state);
        ValueTask AddKeyValueHandler(AddKeyValueCommand c, CancellationToken t) => DoAddKeyValueAsync(c, state);
        ValueTask DeleteKeyValueHandler(DeleteKeyValueCommand c, CancellationToken t) => DoDeleteKeyValueAsync(c, state);
        ValueTask ListSetQueueItemStatusAsync(ListCallbackCommand c, CancellationToken t) => DoListCallbackAsync(c, state);
        ValueTask ListCallbackBatchHandler(ListCallbackBatchCommand c, CancellationToken t) => DoListCallbackBatchAsync(c, state);
        ValueTask SnapshotHandler(LogSnapshotCommand c, CancellationToken t) => DoHandleSnapshotAsync(c, state);

        var interpreter = new Builder()
            .Add(new Func<ListRightPopCommand, CancellationToken, ValueTask>(ListRightPopHandler))
            .Add(new Func<ListLeftPushCommand, CancellationToken, ValueTask>(ListLeftPushHandler))
            .Add(new Func<ListLeftPushBatchCommand, CancellationToken, ValueTask>(ListLeftPushBatchHandler))
            .Add(new Func<AddHashSetCommand, CancellationToken, ValueTask>(AddHashSetHandler))
            .Add(new Func<DeleteHashSetCommand, CancellationToken, ValueTask>(DeleteHashSetHandler))
            .Add(new Func<AddKeyValueCommand, CancellationToken, ValueTask>(AddKeyValueHandler))
            .Add(new Func<DeleteKeyValueCommand, CancellationToken, ValueTask>(DeleteKeyValueHandler))
            .Add(new Func<ListCallbackCommand, CancellationToken, ValueTask>(ListSetQueueItemStatusAsync))
            .Add(new Func<ListCallbackBatchCommand, CancellationToken, ValueTask>(ListCallbackBatchHandler))
            .Add(new Func<LogSnapshotCommand, CancellationToken, ValueTask>(SnapshotHandler))
            .Build();

        return interpreter;
    }
}

