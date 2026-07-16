using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;

namespace SlimData;

internal sealed class SlimDataStateSnapshot(
    ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> hashsets,
    ImmutableDictionary<string, ReadOnlyMemory<byte>> keyValues,
    ImmutableDictionary<string, ImmutableArray<QueueElement>> queues)
{
    internal static readonly SlimDataStateSnapshot Empty = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

    internal ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> Hashsets { get; } = hashsets;
    internal ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues { get; } = keyValues;
    internal ImmutableDictionary<string, ImmutableArray<QueueElement>> Queues { get; } = queues;

    internal long PayloadBytes
    {
        get
        {
            long result = 0L;
            foreach (var value in KeyValues.Values)
                result += value.Length;
            foreach (var hashset in Hashsets.Values)
            foreach (var value in hashset.Values)
                result += value.Length;
            foreach (var queue in Queues.Values)
            foreach (var item in queue)
                result += item.Value.Length;
            return result;
        }
    }
}

public class SlimDataState(
    ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> hashsets,
    ImmutableDictionary<string, ReadOnlyMemory<byte>> keyValues,
    ImmutableDictionary<string, ImmutableArray<QueueElement>> queues)
{
    private SlimDataStateSnapshot _snapshot = new(hashsets, keyValues, queues);

    public ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>> Hashsets
    {
        get => Capture().Hashsets;
        set => Update(snapshot => new(value, snapshot.KeyValues, snapshot.Queues));
    }

    public ImmutableDictionary<string, ReadOnlyMemory<byte>> KeyValues
    {
        get => Capture().KeyValues;
        set => Update(snapshot => new(snapshot.Hashsets, value, snapshot.Queues));
    }

    public ImmutableDictionary<string, ImmutableArray<QueueElement>> Queues
    {
        get => Capture().Queues;
        set => Update(snapshot => new(snapshot.Hashsets, snapshot.KeyValues, value));
    }

    internal SlimDataStateSnapshot Capture() => Volatile.Read(ref _snapshot);

    internal void Replace(SlimDataStateSnapshot snapshot)
        => Interlocked.Exchange(ref _snapshot, snapshot);

    internal void Reset() => Replace(SlimDataStateSnapshot.Empty);

    internal SlimDataPayload CapturePayload()
    {
        var snapshot = Capture();
        return new()
        {
            KeyValues = snapshot.KeyValues,
            Hashsets = snapshot.Hashsets,
            Queues = snapshot.Queues
        };
    }

    private void Update(Func<SlimDataStateSnapshot, SlimDataStateSnapshot> update)
    {
        SlimDataStateSnapshot current;
        SlimDataStateSnapshot replacement;
        do
        {
            current = Capture();
            replacement = update(current);
        } while (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, replacement, current), current));
    }
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
    public QueueHttpTryElement(long startTimeStamp = 0, string idTransaction = "", long endTimeStamp = 0, int httpCode = 0, string reservedIp = "")
    {
        StartTimeStamp = startTimeStamp;
        IdTransaction = idTransaction;
        EndTimeStamp = endTimeStamp;
        HttpCode = httpCode;
        ReservedIp = reservedIp;
    }

    public long StartTimeStamp { get; set; }
    public long EndTimeStamp { get; set; }
    public int HttpCode { get; set; }
    public string IdTransaction { get; set; }
    public string ReservedIp { get; set; }
}

#pragma warning disable CA2252
public class SlimDataInterpreter : CommandInterpreter
{
    public const int DeleteFromQueueCode = 1000;
    public const string TimeToLivePostfix = ":${__slimfaas_ttl__}$";
    public const string HashsetTtlField = "${__slimfaas_ttl__}$";

    public static string TtlKey(string key) => key + TimeToLivePostfix;

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
                    b.Add(new QueueHttpTryElement(
                        listRightPopCommand.NowTicks,
                        listRightPopCommand.IdTransaction,
                        reservedIp: (listRightPopCommand.ReservedIps is { Count: > 0 } && i < listRightPopCommand.ReservedIps.Count)
                            ? listRightPopCommand.ReservedIps[i]
                            : string.Empty));
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
        }

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
            
            if (cmd.ExpireAtUtcTicks.HasValue)
            {
                b[HashsetTtlField] = BitConverter.GetBytes(cmd.ExpireAtUtcTicks.Value);
            }
            else
            {
                b.Remove(HashsetTtlField);
            }
            
            hashsets = hashsets.SetItem(cmd.Key, b.ToImmutable());
        }
        else
        {
            var b = cmd.Value.ToImmutableDictionary().ToBuilder();
            if (cmd.ExpireAtUtcTicks.HasValue)
            {
                b[HashsetTtlField] = BitConverter.GetBytes(cmd.ExpireAtUtcTicks.Value);
            }
            hashsets = hashsets.SetItem(cmd.Key, b.ToImmutable());
        }

        state.Hashsets = hashsets;
        return default;
    }

    [CommandHandler]
    public ValueTask AddKeyValueAsync(AddKeyValueCommand valueCommand, CancellationToken token)
        => DoAddKeyValueAsync(valueCommand, SlimDataState);

    internal static ValueTask DoAddKeyValueAsync(AddKeyValueCommand cmd, SlimDataState state, object? context = null)
    {
        var items = cmd.EffectiveItems();
        for (var i = 0; i < items.Count; i++)
        {
            var result = ResolveKeyValueResult(context, i) ?? new KeyValueCommandResult();
            ApplyKeyValueItem(items[i], state, result);
        }

        return default;
    }

    private static KeyValueCommandResult? ResolveKeyValueResult(object? context, int index)
    {
        return context switch
        {
            KeyValueCommandResult single when index == 0 => single,
            KeyValueCommandBatchContext batch when index < batch.Results.Length => batch.Results[index],
            IReadOnlyList<KeyValueCommandResult> results when index < results.Count => results[index],
            _ => null
        };
    }

    private static void ApplyKeyValueItem(
        AddKeyValueCommand.BatchItem item,
        SlimDataState state,
        KeyValueCommandResult result)
    {
        switch (item.Operation)
        {
            case KeyValueOperation.Set:
                DoSetKeyValue(item, state, result);
                break;
            case KeyValueOperation.IncrementInteger:
                DoIncrementInteger(item, state, result);
                break;
            case KeyValueOperation.IncrementFloat:
                DoIncrementFloat(item, state, result);
                break;
            default:
                DoInvalidKeyValueOperation(result);
                break;
        }
    }

    private static void DoSetKeyValue(
        AddKeyValueCommand.BatchItem item,
        SlimDataState state,
        KeyValueCommandResult result)
    {
        var keyValues = state.KeyValues;

        keyValues = keyValues.SetItem(item.Key, item.Value);
        
        var ttlKey = TtlKey(item.Key);
        if (item.ExpireAtUtcTicks.HasValue)
        {
            var bytes = BitConverter.GetBytes(item.ExpireAtUtcTicks.Value);
            keyValues = keyValues.SetItem(ttlKey, bytes);
        }
        else if (keyValues.ContainsKey(ttlKey)) {
            keyValues = keyValues.Remove(ttlKey);
        }

        state.KeyValues = keyValues;
        result.SetApplied(item.Value);
    }

    private static void DoIncrementInteger(
        AddKeyValueCommand.BatchItem item,
        SlimDataState state,
        KeyValueCommandResult result)
    {
        var keyValues = state.KeyValues;
        var current = 0L;

        if (TryGetActiveValue(ref keyValues, item.Key, item.NowTicks, out var existing) &&
            !TryParseInteger(existing, out current))
        {
            state.KeyValues = keyValues;
            result.SetError(KeyValueCommandStatus.InvalidNumber, "Value is not an integer.");
            return;
        }

        long next;
        try
        {
            next = checked(current + item.IntegerDelta);
        }
        catch (OverflowException)
        {
            state.KeyValues = keyValues;
            result.SetError(KeyValueCommandStatus.Overflow, "Integer increment overflow.");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture));
        state.KeyValues = keyValues.SetItem(item.Key, bytes);
        result.SetApplied(bytes, integerValue: next);
    }

    private static void DoIncrementFloat(
        AddKeyValueCommand.BatchItem item,
        SlimDataState state,
        KeyValueCommandResult result)
    {
        var keyValues = state.KeyValues;
        var current = 0m;

        if (TryGetActiveValue(ref keyValues, item.Key, item.NowTicks, out var existing) &&
            !TryParseDecimal(existing, out current))
        {
            state.KeyValues = keyValues;
            result.SetError(KeyValueCommandStatus.InvalidNumber, "Value is not a decimal number.");
            return;
        }

        decimal next;
        try
        {
            next = checked(current + item.FloatDelta);
        }
        catch (OverflowException)
        {
            state.KeyValues = keyValues;
            result.SetError(KeyValueCommandStatus.Overflow, "Decimal increment overflow.");
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(next.ToString("G29", CultureInfo.InvariantCulture));
        state.KeyValues = keyValues.SetItem(item.Key, bytes);
        result.SetApplied(bytes, decimalValue: next);
    }

    private static void DoInvalidKeyValueOperation(KeyValueCommandResult result)
    {
        result.SetError(KeyValueCommandStatus.InvalidNumber, "Unsupported key/value operation.");
    }

    private static bool TryGetActiveValue(
        ref ImmutableDictionary<string, ReadOnlyMemory<byte>> keyValues,
        string key,
        long nowTicks,
        out ReadOnlyMemory<byte> value)
    {
        var effectiveNowTicks = nowTicks > 0 ? nowTicks : DateTime.UtcNow.Ticks;
        var ttlKey = TtlKey(key);
        if (keyValues.TryGetValue(ttlKey, out var ttlBytes) &&
            TryReadInt64(ttlBytes, out var expireAtTicks) &&
            expireAtTicks <= effectiveNowTicks)
        {
            keyValues = keyValues.Remove(key).Remove(ttlKey);
            value = default;
            return false;
        }

        return keyValues.TryGetValue(key, out value);
    }

    private static bool TryReadInt64(ReadOnlyMemory<byte> bytes, out long value)
    {
        value = 0;
        if (bytes.Length < sizeof(long))
            return false;

        value = BitConverter.ToInt64(bytes.Span);
        return true;
    }

    private static bool TryParseInteger(ReadOnlyMemory<byte> bytes, out long value)
    {
        value = 0;
        try
        {
            var text = StrictUtf8.GetString(bytes.Span);
            return !string.IsNullOrEmpty(text) &&
                   long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryParseDecimal(ReadOnlyMemory<byte> bytes, out decimal value)
    {
        value = 0;
        try
        {
            var text = StrictUtf8.GetString(bytes.Span);
            return !string.IsNullOrEmpty(text) &&
                   decimal.TryParse(
                       text,
                       NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                       CultureInfo.InvariantCulture,
                       out value);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [CommandHandler]
    public ValueTask DeleteKeyValueAsync(DeleteKeyValueCommand valueCommand, CancellationToken token)
        => DoDeleteKeyValueAsync(valueCommand, SlimDataState);

    internal static ValueTask DoDeleteKeyValueAsync(DeleteKeyValueCommand cmd, SlimDataState state)
    {
        var keyValues = state.KeyValues;
        
        if (keyValues.ContainsKey(cmd.Key)){
            keyValues = keyValues.Remove(cmd.Key);
        }
        var ttlKey = TtlKey(cmd.Key);
        if (keyValues.ContainsKey(ttlKey)){
            keyValues = keyValues.Remove(ttlKey);
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

    public static CommandInterpreter InitInterpreter(SlimDataState state)
    {
        ValueTask ListRightPopHandler(ListRightPopCommand c, CancellationToken t) => DoListRightPopAsync(c, state);
        ValueTask ListLeftPushBatchHandler(ListLeftPushBatchCommand c, CancellationToken t) => DoListLeftPushBatchAsync(c, state);
        ValueTask AddHashSetHandler(AddHashSetCommand c, CancellationToken t) => DoAddHashSetAsync(c, state);
        ValueTask DeleteHashSetHandler(DeleteHashSetCommand c, CancellationToken t) => DoDeleteHashSetAsync(c, state);
        ValueTask AddKeyValueHandler(AddKeyValueCommand c, object? context, CancellationToken t) =>
            DoAddKeyValueAsync(c, state, context);
        ValueTask DeleteKeyValueHandler(DeleteKeyValueCommand c, CancellationToken t) => DoDeleteKeyValueAsync(c, state);
        ValueTask ListSetQueueItemStatusAsync(ListCallbackCommand c, CancellationToken t) => DoListCallbackAsync(c, state);
        ValueTask ListCallbackBatchHandler(ListCallbackBatchCommand c, CancellationToken t) => DoListCallbackBatchAsync(c, state);

        var interpreter = new Builder()
            .Add(new Func<ListRightPopCommand, CancellationToken, ValueTask>(ListRightPopHandler))
            .Add(new Func<ListLeftPushBatchCommand, CancellationToken, ValueTask>(ListLeftPushBatchHandler))
            .Add(new Func<AddHashSetCommand, CancellationToken, ValueTask>(AddHashSetHandler))
            .Add(new Func<DeleteHashSetCommand, CancellationToken, ValueTask>(DeleteHashSetHandler))
            .Add(new Func<AddKeyValueCommand, object?, CancellationToken, ValueTask>(AddKeyValueHandler))
            .Add(new Func<DeleteKeyValueCommand, CancellationToken, ValueTask>(DeleteKeyValueHandler))
            .Add(new Func<ListCallbackCommand, CancellationToken, ValueTask>(ListSetQueueItemStatusAsync))
            .Add(new Func<ListCallbackBatchCommand, CancellationToken, ValueTask>(ListCallbackBatchHandler))
            .Build();

        return interpreter;
    }
}
#pragma warning restore CA2252
