using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using MemoryPack;
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
public static class SlimDataInterpreter
{
    public const int DeleteFromQueueCode = 1000;
    public const string TimeToLivePostfix = ":${__slimfaas_ttl__}$";
    public const string HashsetTtlField = "${__slimfaas_ttl__}$";

    public static string TtlKey(string key) => key + TimeToLivePostfix;

    internal static ValueTask DoListRightPopAsync(ListRightPopCommand listRightPopCommand, SlimDataState slimDataState)
    {
        var queues = slimDataState.Queues;
        if (!queues.TryGetValue(listRightPopCommand.Key, out var queue))
            return default;

        var nowTicks = listRightPopCommand.NowTicks;
        var idTransaction = listRightPopCommand.IdTransaction;
        var maximum = listRightPopCommand.Count;

        // Single pass over the queue: expire the tries that timed out, drop the finished
        // elements, detect an already applied transaction and pick the elements to
        // dispatch. Element state only depends on the element itself and on nowTicks,
        // so evaluating it once per element is equivalent to the historical
        // timeout / finished / transaction / available passes.
        ImmutableArray<QueueElement>.Builder? kept = null;
        QueueElement[]? candidates = null;
        var candidateCount = 0;
        var transactionAlreadyApplied = false;

        for (var i = 0; i < queue.Length; i++)
        {
            var element = queue[i];
            var tries = element.RetryQueueElements;
            if (!tries.IsDefaultOrEmpty)
            {
                var last = tries[^1];
                if (last.EndTimeStamp == 0 && last.StartTimeStamp + element.HttpTimeoutTicks <= nowTicks)
                {
                    last.EndTimeStamp = nowTicks;
                    last.HttpCode = 504;
                }
            }

            var elementState = element.GetState(nowTicks);
            if (elementState == QueueElementState.Finished)
            {
                kept ??= CopyPrefix(queue, i);
                continue;
            }

            kept?.Add(element);

            if (transactionAlreadyApplied)
                continue;

            if (!tries.IsDefaultOrEmpty && tries[^1].IdTransaction == idTransaction)
            {
                transactionAlreadyApplied = true;
                continue;
            }

            if (elementState == QueueElementState.Available && candidateCount < maximum)
            {
                candidates ??= new QueueElement[Math.Min(maximum, queue.Length - i)];
                candidates[candidateCount++] = element;
            }
        }

        if (kept is not null)
            queue = kept.DrainToImmutable();

        if (!transactionAlreadyApplied)
        {
            var reservedIps = listRightPopCommand.ReservedIps;
            for (var i = 0; i < candidateCount; i++)
            {
                var element = candidates![i];
                var attempt = new QueueHttpTryElement(
                    nowTicks,
                    idTransaction,
                    reservedIp: reservedIps is { Count: > 0 } && i < reservedIps.Count
                        ? reservedIps[i]
                        : string.Empty);
                var tries = element.RetryQueueElements;
                element.RetryQueueElements = tries.IsDefault ? [attempt] : tries.Add(attempt);
            }
        }

        slimDataState.Queues = queues.SetItem(listRightPopCommand.Key, queue);
        return default;
    }

    internal static ValueTask DoListLeftPushBatchAsync(ListLeftPushBatchCommand cmd, SlimDataState state)
    {
        var queues = state.Queues;
        if (cmd.Items is null || cmd.Items.Count == 0)
            return default;

        var pendingQueues = new Dictionary<
            string,
            (ImmutableArray<QueueElement> Existing, List<QueueElement> Added, HashSet<string> Identifiers)>(StringComparer.Ordinal);
        foreach (var item in cmd.Items)
        {
            if (!pendingQueues.TryGetValue(item.Key, out var pending))
            {
                ImmutableArray<QueueElement> existing = queues.TryGetValue(item.Key, out var current)
                    ? current
                    : ImmutableArray<QueueElement>.Empty;
                var identifiers = new HashSet<string>(existing.Length, StringComparer.Ordinal);
                foreach (var element in existing)
                    identifiers.Add(element.Id);
                pending = (existing, [], identifiers);
                pendingQueues.Add(item.Key, pending);
            }

            if (!pending.Identifiers.Add(item.Identifier))
                continue;

            pending.Added.Add(new QueueElement(
                item.Value,
                item.Identifier,
                item.NowTicks,
                item.RetryTimeout,
                item.Retries is null ? ImmutableArray<int>.Empty : item.Retries.ToImmutableArray(),
                ImmutableArray<QueueHttpTryElement>.Empty,
                item.HttpStatusCodesWorthRetrying is null ? ImmutableHashSet<int>.Empty : item.HttpStatusCodesWorthRetrying.ToImmutableHashSet()
            ));
        }

        foreach ((string key, var pending) in pendingQueues)
        {
            if (pending.Added.Count > 0)
                queues = queues.SetItem(key, pending.Existing.AddRange(CollectionsMarshal.AsSpan(pending.Added)));
        }

        state.Queues = queues;
        return default;
    }

    internal static ValueTask DoListCallbackAsync(ListCallbackCommand cmd, SlimDataState state)
    {
        var queues = state.Queues;
        if (!queues.TryGetValue(cmd.Key, out var queue))
            return default;

        state.Queues = queues.SetItem(cmd.Key, ApplyCallbacks(queue, cmd.CallbackElements, cmd.NowTicks));
        return default;
    }

    internal static ValueTask DoListCallbackBatchAsync(ListCallbackBatchCommand batch, SlimDataState state)
    {
        var queues = state.Queues;
        if (batch.Items is null || batch.Items.Count == 0)
            return default;

        foreach (var item in batch.Items)
        {
            if (!queues.TryGetValue(item.Key, out var queue) || item.CallbackElements is null || item.CallbackElements.Count == 0)
                continue;

            queues = queues.SetItem(item.Key, ApplyCallbacks(queue, item.CallbackElements, item.NowTicks));
        }

        state.Queues = queues;
        return default;
    }

    /// <summary>
    /// Applies HTTP callbacks to a queue in one pass: O(N + M) for N elements and
    /// M callbacks, with allocations proportional to M (the callback index) plus one
    /// right-sized array when at least one element is evicted. Callbacks carrying the
    /// same identifier are applied in command order until the element is evicted.
    /// Returns the same array instance when nothing was evicted.
    /// </summary>
    private static ImmutableArray<QueueElement> ApplyCallbacks(
        ImmutableArray<QueueElement> queue,
        IList<CallbackElement> callbacks,
        long nowTicks)
    {
        if (queue.IsDefaultOrEmpty || callbacks.Count == 0)
            return queue;

        // Index callbacks by identifier; duplicates are chained in command order.
        // Built backwards so that first[id] is the earliest callback and next[] walks forward.
        CallbackElement? single = callbacks.Count == 1 ? callbacks[0] : null;
        Dictionary<string, int>? first = null;
        int[]? next = null;
        if (single is null)
        {
            first = new Dictionary<string, int>(callbacks.Count, StringComparer.Ordinal);
            next = new int[callbacks.Count];
            for (var i = callbacks.Count - 1; i >= 0; i--)
            {
                var identifier = callbacks[i].Identifier;
                next[i] = first.TryGetValue(identifier, out var following) ? following : -1;
                first[identifier] = i;
            }
        }

        ImmutableArray<QueueElement>.Builder? kept = null;
        for (var i = 0; i < queue.Length; i++)
        {
            var element = queue[i];
            var evicted = false;
            if (single is not null)
            {
                if (single.Identifier == element.Id)
                    evicted = ApplyCallback(element, single.HttpCode, nowTicks);
            }
            else if (first!.TryGetValue(element.Id, out var index))
            {
                for (; index >= 0 && !evicted; index = next![index])
                    evicted = ApplyCallback(element, callbacks[index].HttpCode, nowTicks);
            }

            if (evicted)
            {
                kept ??= CopyPrefix(queue, i);
                continue;
            }

            kept?.Add(element);
        }

        return kept is null ? queue : kept.DrainToImmutable();
    }

    /// <summary>Records one HTTP result on the element; returns true when the element must leave the queue.</summary>
    private static bool ApplyCallback(QueueElement element, int httpCode, long nowTicks)
    {
        if (httpCode == DeleteFromQueueCode)
            return true;

        var tries = element.RetryQueueElements;
        if (tries.IsDefaultOrEmpty)
            return false;

        var last = tries[^1];
        last.EndTimeStamp = nowTicks;
        last.HttpCode = httpCode;
        return element.IsFinished(nowTicks);
    }

    /// <summary>Builder holding the first <paramref name="length"/> elements of <paramref name="queue"/>, sized for at least one eviction.</summary>
    private static ImmutableArray<QueueElement>.Builder CopyPrefix(ImmutableArray<QueueElement> queue, int length)
    {
        var builder = ImmutableArray.CreateBuilder<QueueElement>(queue.Length - 1);
        builder.AddRange(queue, length);
        return builder;
    }

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
        keyValues = keyValues.SetItem(item.Key, bytes);
        if (item.ExpireAtUtcTicks.HasValue)
        {
            var ttlBytes = BitConverter.GetBytes(item.ExpireAtUtcTicks.Value);
            keyValues = keyValues.SetItem(TtlKey(item.Key), ttlBytes);
        }
        state.KeyValues = keyValues;
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
        keyValues = keyValues.SetItem(item.Key, bytes);
        if (item.ExpireAtUtcTicks.HasValue)
        {
            var ttlBytes = BitConverter.GetBytes(item.ExpireAtUtcTicks.Value);
            keyValues = keyValues.SetItem(TtlKey(item.Key), ttlBytes);
        }
        state.KeyValues = keyValues;
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

    internal static ValueTask DoExecuteBatchAsync(
        ExecuteBatchCommand command,
        SlimDataState state,
        object? context = null)
    {
        var envelope = SlimDataRaftBatchEnvelopeCodec.Deserialize(command.Payload.Span);

        var responses = new SlimDataCommandBatchResponse[envelope.Requests.Length];
        for (var i = 0; i < envelope.Requests.Length; i++)
            responses[i] = ApplyBatchRequest(envelope.Requests[i], state);

        if (context is SlimDataCommandBatchContext batchContext)
            batchContext.SetResponses(responses);

        return default;
    }

    private static SlimDataCommandBatchResponse ApplyBatchRequest(
        SlimDataCommandBatchRequest request,
        SlimDataState state)
    {
        var sessionKey = SlimDataCommandBatchValidator.ProducerSessionKeyPrefix + request.ProducerId;
        if (state.KeyValues.TryGetValue(sessionKey, out var sessionBytes))
        {
            SlimDataProducerSession? session;
            try
            {
                session = MemoryPackSerializer.Deserialize<SlimDataProducerSession>(sessionBytes.Span);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("The persisted SlimData producer session is invalid.", ex);
            }

            if (session is not null)
            {
                if (string.Equals(session.GenerationId, request.GenerationId, StringComparison.Ordinal))
                {
                    if (session.Sequence == request.Sequence &&
                        string.Equals(session.RequestId, request.RequestId, StringComparison.Ordinal))
                    {
                        var duplicate = DeserializeCachedResponse(session.Response);
                        duplicate.Duplicate = true;
                        return duplicate;
                    }

                    if (request.Sequence != session.Sequence + 1)
                        return CreateSequenceGapResponse(request, session.Sequence + 1);
                }
                else if (request.Sequence != 1)
                {
                    return CreateSequenceGapResponse(request, 1);
                }
            }
        }
        else if (request.Sequence != 1)
        {
            return CreateSequenceGapResponse(request, 1);
        }

        var results = new SlimDataBatchOperationResult[request.Operations.Length];
        for (var i = 0; i < request.Operations.Length;)
        {
            if (request.Operations[i].Kind != SlimDataBatchOperationKind.ListLeftPush)
            {
                results[i] = ApplyBatchOperation(request.Operations[i], state);
                i++;
                continue;
            }

            var end = i + 1;
            while (end < request.Operations.Length &&
                   request.Operations[end].Kind == SlimDataBatchOperationKind.ListLeftPush)
            {
                end++;
            }

            var items = new List<ListLeftPushBatchCommand.BatchItem>(end - i);
            for (var index = i; index < end; index++)
            {
                SlimDataBatchOperation operation = request.Operations[index];
                items.Add(new ListLeftPushBatchCommand.BatchItem
                {
                    Key = operation.Key,
                    Identifier = operation.ElementId,
                    NowTicks = operation.NowTicks,
                    RetryTimeout = operation.RetryTimeoutSeconds,
                    Retries = operation.Retries.ToList(),
                    HttpStatusCodesWorthRetrying = operation.HttpStatusRetries.ToList(),
                    Value = operation.Value
                });
                results[index] = new SlimDataBatchOperationResult
                {
                    Kind = operation.Kind,
                    RequestId = operation.RequestId,
                    Applied = true,
                    ElementId = operation.ElementId
                };
            }

            DoListLeftPushBatchAsync(new ListLeftPushBatchCommand { Items = items }, state);
            i = end;
        }

        var response = new SlimDataCommandBatchResponse
        {
            ProducerId = request.ProducerId,
            Sequence = request.Sequence,
            RequestId = request.RequestId,
            ExpectedSequence = request.Sequence + 1,
            Results = results
        };

        var responseBytes = MemoryPackSerializer.Serialize(response);
        var producerSession = new SlimDataProducerSession
        {
            GenerationId = request.GenerationId,
            Sequence = request.Sequence,
            RequestId = request.RequestId,
            Response = responseBytes
        };
        state.KeyValues = state.KeyValues.SetItem(
            sessionKey,
            MemoryPackSerializer.Serialize(producerSession));

        return response;
    }

    private static SlimDataCommandBatchResponse DeserializeCachedResponse(byte[] response)
    {
        try
        {
            return MemoryPackSerializer.Deserialize<SlimDataCommandBatchResponse>(response)
                ?? throw new InvalidDataException("The cached SlimData producer response is null.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("The cached SlimData producer response is invalid.", ex);
        }
    }

    private static SlimDataCommandBatchResponse CreateSequenceGapResponse(
        SlimDataCommandBatchRequest request,
        long expectedSequence)
        => new()
        {
            ProducerId = request.ProducerId,
            Sequence = request.Sequence,
            RequestId = request.RequestId,
            SequenceGap = true,
            ExpectedSequence = expectedSequence,
            ErrorMessage =
                $"Producer {request.ProducerId} sent sequence {request.Sequence}; expected {expectedSequence}.",
            Results = []
        };

    private static SlimDataBatchOperationResult ApplyBatchOperation(
        SlimDataBatchOperation operation,
        SlimDataState state)
    {
        var result = new SlimDataBatchOperationResult
        {
            Kind = operation.Kind,
            RequestId = operation.RequestId,
            Applied = true
        };

        switch (operation.Kind)
        {
            case SlimDataBatchOperationKind.KeyValue:
            {
                var keyValueResult = new KeyValueCommandResult();
                DoAddKeyValueAsync(
                    new AddKeyValueCommand
                    {
                        Items =
                        [
                            new AddKeyValueCommand.BatchItem
                            {
                                Operation = operation.KeyValueOperation,
                                Key = operation.Key,
                                Value = operation.Value,
                                IntegerDelta = operation.IntegerDelta,
                                FloatDelta = operation.FloatDelta,
                                ExpireAtUtcTicks = operation.ExpireAtUtcTicks,
                                NowTicks = operation.NowTicks
                            }
                        ]
                    },
                    state,
                    keyValueResult);
                result.KeyValueResult = keyValueResult;
                result.Applied = keyValueResult.Applied;
                result.ErrorMessage = keyValueResult.ErrorMessage;
                break;
            }

            case SlimDataBatchOperationKind.DeleteKeyValue:
                DoDeleteKeyValueAsync(new DeleteKeyValueCommand { Key = operation.Key }, state);
                break;

            case SlimDataBatchOperationKind.AddHashSet:
                DoAddHashSetAsync(
                    new AddHashSetCommand
                    {
                        Key = operation.Key,
                        Value = operation.HashValues!
                            .ToDictionary(
                                static item => item.Key,
                                static item => (ReadOnlyMemory<byte>)item.Value),
                        ExpireAtUtcTicks = operation.ExpireAtUtcTicks
                    },
                    state);
                break;

            case SlimDataBatchOperationKind.DeleteHashSet:
                DoDeleteHashSetAsync(
                    new DeleteHashSetCommand
                    {
                        Key = operation.Key,
                        DictionaryKey = operation.DictionaryKey
                    },
                    state);
                break;

            case SlimDataBatchOperationKind.ListLeftPush:
                DoListLeftPushBatchAsync(
                    new ListLeftPushBatchCommand
                    {
                        Items =
                        [
                            new ListLeftPushBatchCommand.BatchItem
                            {
                                Key = operation.Key,
                                Identifier = operation.ElementId,
                                NowTicks = operation.NowTicks,
                                RetryTimeout = operation.RetryTimeoutSeconds,
                                Retries = operation.Retries.ToList(),
                                HttpStatusCodesWorthRetrying = operation.HttpStatusRetries.ToList(),
                                Value = operation.Value
                            }
                        ]
                    },
                    state);
                result.ElementId = operation.ElementId;
                break;

            case SlimDataBatchOperationKind.ListRightPop:
                DoListRightPopAsync(
                    new ListRightPopCommand
                    {
                        Key = operation.Key,
                        Count = operation.Count,
                        NowTicks = operation.NowTicks,
                        IdTransaction = operation.TransactionId,
                        ReservedIps = operation.ReservedIps.ToList()
                    },
                    state);
                result.QueueItems = GetPoppedQueueItems(operation, state);
                break;

            case SlimDataBatchOperationKind.ListCallback:
                DoListCallbackAsync(
                    new ListCallbackCommand
                    {
                        Key = operation.Key,
                        NowTicks = operation.NowTicks,
                        CallbackElements = operation.CallbackItems
                            .Select(static item => new CallbackElement(item.Id, item.HttpCode))
                            .ToList()
                    },
                    state);
                break;

            default:
                throw new InvalidDataException($"Unsupported SlimData operation kind {(byte)operation.Kind}.");
        }

        return result;
    }

    private static QueueData[] GetPoppedQueueItems(
        SlimDataBatchOperation operation,
        SlimDataState state)
    {
        if (!state.Queues.TryGetValue(operation.Key, out var queue))
            return [];

        return queue
            .GetQueueRunningElement(operation.NowTicks)
            .Where(item =>
                !item.RetryQueueElements.IsDefaultOrEmpty &&
                item.RetryQueueElements[^1].IdTransaction == operation.TransactionId)
            .Select(static item => new QueueData(
                item.Id,
                GetQueueValueBuffer(item.Value),
                item.NumberOfTries(),
                item.IsLastTry(),
                item.GetLastRetryTimeTicks(),
                item.GetHttpTimeoutTicks(),
                item.GetLastReservedIp()))
            .ToArray();
    }

    private static byte[] GetQueueValueBuffer(ReadOnlyMemory<byte> value)
    {
        if (MemoryMarshal.TryGetArray(value, out ArraySegment<byte> segment) &&
            segment.Offset == 0 &&
            segment.Count == segment.Array!.Length)
        {
            return segment.Array;
        }

        return value.ToArray();
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
        ValueTask ExecuteBatchHandler(ExecuteBatchCommand c, object? context, CancellationToken t) =>
            DoExecuteBatchAsync(c, state, context);

        var interpreter = new CommandInterpreter.Builder()
            .Add(new Func<ListRightPopCommand, CancellationToken, ValueTask>(ListRightPopHandler))
            .Add(new Func<ListLeftPushBatchCommand, CancellationToken, ValueTask>(ListLeftPushBatchHandler))
            .Add(new Func<AddHashSetCommand, CancellationToken, ValueTask>(AddHashSetHandler))
            .Add(new Func<DeleteHashSetCommand, CancellationToken, ValueTask>(DeleteHashSetHandler))
            .Add(new Func<AddKeyValueCommand, object?, CancellationToken, ValueTask>(AddKeyValueHandler))
            .Add(new Func<DeleteKeyValueCommand, CancellationToken, ValueTask>(DeleteKeyValueHandler))
            .Add(new Func<ListCallbackCommand, CancellationToken, ValueTask>(ListSetQueueItemStatusAsync))
            .Add(new Func<ListCallbackBatchCommand, CancellationToken, ValueTask>(ListCallbackBatchHandler))
            .Add(new Func<ExecuteBatchCommand, object?, CancellationToken, ValueTask>(ExecuteBatchHandler))
            .Build();

        return interpreter;
    }
}
#pragma warning restore CA2252
