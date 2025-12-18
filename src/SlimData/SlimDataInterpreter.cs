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
        ImmutableArray<int> timeoutRetriesSeconds,
        ImmutableArray<QueueHttpTryElement> retryQueueElements,
        ImmutableHashSet<int> httpStatusRetries
    )
    {
        Value = value;
        Id = id;
        InsertTimeStamp = insertTimeStamp;
        HttpTimeoutSeconds = httpTimeoutSeconds;
        TimeoutRetriesSeconds = timeoutRetriesSeconds;
        RetryQueueElements = retryQueueElements;
        HttpStatusRetries = httpStatusRetries;
        HttpTimeoutTicks = (long)httpTimeoutSeconds * TimeSpan.TicksPerSecond;
    }

    public ReadOnlyMemory<byte> Value { get; }
    public string Id { get; }
    public long InsertTimeStamp { get; }
    public int HttpTimeoutSeconds { get; }
    public long HttpTimeoutTicks { get; }

    public ImmutableArray<int> TimeoutRetriesSeconds { get; }
    public ImmutableArray<QueueHttpTryElement> RetryQueueElements { get; set; }
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

#pragma warning disable CA2252
public class SlimDataInterpreter : CommandInterpreter
{
    public const int DeleteFromQueueCode = 1000;
    private const string TimeToLiveSuffix = "${slimfaas-timetolive}$";
    private const string HashsetTtlField = "__ttl__";

    private static string TtlKey(string key) => key + TimeToLiveSuffix;

    public SlimDataState SlimDataState = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
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

            var queueTimeoutElements = queue.GetQueueTimeoutElement(nowTicks);
            for (int i = 0; i < queueTimeoutElements.Length; i++)
            {
                var qe = queueTimeoutElements[i];
                if (qe.RetryQueueElements.IsDefaultOrEmpty) continue;
                var last = qe.RetryQueueElements[^1];
                last.EndTimeStamp = nowTicks;
                last.HttpCode = 504;
            }

            var finished = queue.GetQueueFinishedElement(nowTicks);
            if (!finished.IsDefaultOrEmpty)
            {
                var keep = ImmutableArray.CreateBuilder<QueueElement>(queue.Length - finished.Length);
                for (int i = 0; i < queue.Length; i++)
                {
                    var e = queue[i];
                    bool isFinished = false;
                    for (int j = 0; j < finished.Length; j++)
                    {
                        if (ReferenceEquals(e, finished[j])) { isFinished = true; break; }
                    }
                    if (!isFinished) keep.Add(e);
                }
                queue = keep.MoveToImmutable();
            }

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

        var work = arr;

        for (int i = 0; i < cmd.CallbackElements.Count; i++)
        {
            var cb = cmd.CallbackElements[i];

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

        if (hashsets.TryGetValue(cmd.Key, out var existing))
        {
            var b = existing.ToBuilder();
            foreach (var kv in cmd.Value) b[kv.Key] = kv.Value;
            hashsets = hashsets.SetItem(cmd.Key, b.ToImmutable());
        }
        else
        {
            hashsets = hashsets.SetItem(cmd.Key, cmd.Value.ToImmutableDictionary());
        }

        var ttlKey = TtlKey(cmd.Key);
        if (cmd.ExpireAtUtcTicks.HasValue)
        {
            var meta = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .SetItem(HashsetTtlField, BitConverter.GetBytes(cmd.ExpireAtUtcTicks.Value));
            hashsets = hashsets.SetItem(ttlKey, meta);
        }
        else
        {
            if (hashsets.ContainsKey(ttlKey))
                hashsets = hashsets.Remove(ttlKey);
        }

        state.Hashsets = hashsets;
        return default;
    }

    [CommandHandler]
    public ValueTask AddKeyValueAsync(AddKeyValueCommand valueCommand, CancellationToken token)
        => DoAddKeyValueAsync(valueCommand, SlimDataState);

    internal static ValueTask DoAddKeyValueAsync(AddKeyValueCommand cmd, SlimDataState state)
    {
        var keyValues = state.KeyValues;

        keyValues = keyValues.SetItem(cmd.Key, cmd.Value);
        
        var ttlKey = TtlKey(cmd.Key);
        Console.WriteLine("[AddKeyValue] Key: {0}, TTL: {1}", cmd.Key, cmd.ExpireAtUtcTicks.HasValue ? cmd.ExpireAtUtcTicks.Value.ToString() : "null");
        if (cmd.ExpireAtUtcTicks.HasValue)
        {
            var bytes = BitConverter.GetBytes(cmd.ExpireAtUtcTicks.Value);
            keyValues = keyValues.SetItem(ttlKey, bytes);
        }
        else if (keyValues.ContainsKey(ttlKey)) {
            keyValues = keyValues.Remove(ttlKey);
        }

        state.KeyValues = keyValues;
        return default;
    }

    [CommandHandler]
    public ValueTask DeleteKeyValueAsync(DeleteKeyValueCommand valueCommand, CancellationToken token)
        => DoDeleteKeyValueAsync(valueCommand, SlimDataState);

    internal static ValueTask DoDeleteKeyValueAsync(DeleteKeyValueCommand cmd, SlimDataState state)
    {
        var keyValues = state.KeyValues;
        
        if (keyValues.ContainsKey(cmd.Key)){
            keyValues = keyValues.Remove(cmd.Key);
            Console.WriteLine("[DeleteKeyValue] Key: {0} deleted", cmd.Key);
        }
        var ttlKey = TtlKey(cmd.Key);
        if (keyValues.ContainsKey(ttlKey)){
            keyValues = keyValues.Remove(ttlKey);
            Console.WriteLine("[DeleteKeyValue] TTL Key: {0} deleted", ttlKey);
        }
        state.KeyValues = keyValues;
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

        var ttlKey = TtlKey(key);

        if (string.IsNullOrEmpty(cmd.DictionaryKey))
        {
            state.Hashsets = state.Hashsets.Remove(key);
            if (state.Hashsets.ContainsKey(ttlKey))
                state.Hashsets = state.Hashsets.Remove(ttlKey);
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
        state.KeyValues = command.keysValues.ToImmutableDictionary();

        foreach (var q in command.queues)
        {
            var builder = ImmutableArray.CreateBuilder<QueueElement>();
            var arr = builder.ToImmutable();
            state.Queues = state.Queues.SetItem(q.Key, arr);
        }

        var newHashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty;
        foreach (var hs in command.hashsets)
            newHashsets = newHashsets.SetItem(hs.Key, hs.Value.ToImmutableDictionary());
        state.Hashsets = newHashsets;
        
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
#pragma warning restore CA2252
