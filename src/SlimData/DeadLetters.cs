using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using MemoryPack;
using SlimData.Commands;

namespace SlimData;

[MemoryPackable]
public sealed partial class DeadLetterPolicy
{
    public bool Enabled { get; set; }
    public int MaxAgeSeconds { get; set; } = 604800;
    public int MaxLength { get; set; } = 10000;
}

[MemoryPackable]
public sealed partial class DeadLetterEntry
{
    public string Id { get; set; } = "";
    public string Function { get; set; } = "";
    public string OriginalElementId { get; set; } = "";
    public long AcceptedAtTicks { get; set; }
    public long FailedAtTicks { get; set; }
    public long ExpiresAtTicks { get; set; }
    public int Attempts { get; set; }
    public int? LastHttpStatus { get; set; }
    public string FailureReason { get; set; } = "";
    public byte[] Value { get; set; } = [];
    public string? ReplayOfDeadLetterId { get; set; }
    public string? RootElementId { get; set; }
}

[MemoryPackable]
public sealed partial class DeadLetterActive
{
    public bool AwaitingCallback { get; set; }
    public string Source { get; set; } = "Http";
    public string? ReplayOfDeadLetterId { get; set; }
    public string? RootElementId { get; set; }
}

[MemoryPackable]
public sealed partial class DeadLetterReceipt
{
    public string DeadLetterId { get; set; } = "";
    public string OriginalElementId { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string Function { get; set; } = "";
    public long ExpiresAtTicks { get; set; }
}

[MemoryPackable]
public sealed partial class DeadLetterStatistics
{
    public long Captured { get; set; }
    public long Requeued { get; set; }
    public long Deleted { get; set; }
    public long Expired { get; set; }
    public long Evicted { get; set; }
    public long Revision { get; set; }
    public DeadLetterTransition[] RecentTransitions { get; set; } = [];
}

[MemoryPackable]
public sealed partial class DeadLetterTransition
{
    public long Revision { get; set; }
    public string Type { get; set; } = "";
    public string Function { get; set; } = "";
    public string DeadLetterId { get; set; } = "";
    public string OriginalElementId { get; set; } = "";
    public string? ElementId { get; set; }
}

public enum DeadLetterAction { Policy, Complete, AwaitingCallback, Sweep, Requeue, Delete }

[MemoryPackable]
public sealed partial class DeadLetterMutation
{
    public DeadLetterAction Action { get; set; }
    public string Function { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string NewElementId { get; set; } = "";
    public DeadLetterPolicy? Policy { get; set; }
    public int HttpStatus { get; set; }
    public string Source { get; set; } = "Http";
    public long NowTicks { get; set; }
    public int RetryTimeoutSeconds { get; set; }
    public int[] Retries { get; set; } = [];
    public int[] HttpStatusRetries { get; set; } = [];
}

[MemoryPackable]
public sealed partial class DeadLetterMutationResult
{
    public int Status { get; set; } = 200;
    public DeadLetterReceipt? Receipt { get; set; }
}

public static class DeadLetterKeys
{
    public const string PolicyPrefix = "\0dlq:policy:";
    public static string Policy(string function) => PolicyPrefix + function;
    public static string Messages(string function) => "\0dlq:messages:" + function;
    public static string Payloads(string function) => "\0dlq:payloads:" + function;
    public static string Receipts(string function) => "\0dlq:receipts:" + function;
    public static string Active(string function) => "\0dlq:active:" + function;
    public static string Stats(string function) => "\0dlq:stats:" + function;

    public static IEnumerable<string> GetPayloadOwnerIds(SlimDataPayload payload)
    {
        foreach (var (key, values) in payload.Hashsets)
        {
            if (key.StartsWith("\0dlq:messages:", StringComparison.Ordinal))
                foreach (var value in values.Values)
                {
                    var entry = MemoryPackSerializer.Deserialize<DeadLetterEntry>(value.Span)!;
                    yield return entry.OriginalElementId;
                    if (entry.RootElementId is not null)
                        yield return entry.RootElementId;
                }
            if (key.StartsWith("\0dlq:active:", StringComparison.Ordinal))
                foreach (var value in values.Values)
                {
                    var active = MemoryPackSerializer.Deserialize<DeadLetterActive>(value.Span)!;
                    if (active.RootElementId is not null)
                        yield return active.RootElementId;
                }
        }
    }
}

internal static class DeadLetterStateMachine
{
    private static T? Read<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(SlimDataState state, string key) where T : class =>
        state.KeyValues.TryGetValue(key, out var bytes) ? MemoryPackSerializer.Deserialize<T>(bytes.Span) : null;

    private static ImmutableDictionary<string, ReadOnlyMemory<byte>> Get(SlimDataState state, string key) =>
        state.Hashsets.GetValueOrDefault(key) ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
    private static ImmutableArray<QueueElement> Queue(SlimDataState state, string key) =>
        state.Queues.TryGetValue(key, out var queue) ? queue : [];

    private static void Put(SlimDataState state, string key, string field, byte[] value) =>
        state.Hashsets = state.Hashsets.SetItem(key, Get(state, key).SetItem(field, value));

    private static void Remove(SlimDataState state, string key, string field) =>
        state.Hashsets = state.Hashsets.SetItem(key, Get(state, key).Remove(field));

    private static DeadLetterActive Active(SlimDataState state, string function, string id) =>
        Get(state, DeadLetterKeys.Active(function)).TryGetValue(id, out var bytes)
            ? MemoryPackSerializer.Deserialize<DeadLetterActive>(bytes.Span)!
            : new();

    private static void Increment(SlimDataState state, string function, Action<DeadLetterStatistics> change)
    {
        var stats = Read<DeadLetterStatistics>(state, DeadLetterKeys.Stats(function)) ?? new();
        change(stats);
        stats.Revision++;
        state.KeyValues = state.KeyValues.SetItem(DeadLetterKeys.Stats(function), MemoryPackSerializer.Serialize(stats));
    }

    private static void Transition(SlimDataState state, DeadLetterEntry entry, string type, string? elementId = null)
    {
        Increment(state, entry.Function, stats =>
        {
            switch (type)
            {
                case "dead_lettered": stats.Captured++; break;
                case "dead_letter_requeued": stats.Requeued++; break;
                case "dead_letter_deleted": stats.Deleted++; break;
                case "dead_letter_expired": stats.Expired++; break;
                case "dead_letter_evicted": stats.Evicted++; break;
                default: throw new InvalidDataException("Unknown DLQ transition.");
            }
            stats.RecentTransitions = stats.RecentTransitions.TakeLast(127).Append(new DeadLetterTransition
            {
                Revision = stats.Revision + 1, Type = type, Function = entry.Function, DeadLetterId = entry.Id,
                OriginalElementId = entry.OriginalElementId, ElementId = elementId
            }).ToArray();
        });
    }

    private static void RemoveEntry(SlimDataState state, DeadLetterEntry entry)
    {
        Remove(state, DeadLetterKeys.Messages(entry.Function), entry.Id);
        Remove(state, DeadLetterKeys.Payloads(entry.Function), entry.Id);
    }

    internal static DeadLetterMutationResult Apply(DeadLetterMutation mutation, SlimDataState state)
    {
        var snapshot = state.Capture();
        var staged = new SlimDataState(snapshot.Hashsets, snapshot.KeyValues, snapshot.Queues);
        var result = ApplyCore(mutation, staged);
        state.Replace(staged.Capture());
        return result;
    }

    internal static bool IsManaged(SlimDataState state, string key) =>
        key.StartsWith("Queue:", StringComparison.Ordinal) &&
        state.KeyValues.ContainsKey(DeadLetterKeys.Policy(key["Queue:".Length..]));

    internal static SlimDataState StageQueue(SlimDataState state, string key)
    {
        var snapshot = state.Capture();
        var queues = snapshot.Queues;
        if (queues.TryGetValue(key, out var queue))
            queues = queues.SetItem(key, queue.Select(Clone).ToImmutableArray());
        return new(snapshot.Hashsets, snapshot.KeyValues, queues);
    }

    private static QueueElement Clone(QueueElement original) =>
        new(original.Value, original.Id, original.InsertTimeStamp, original.HttpTimeoutSeconds,
            original.TimeoutRetriesSeconds,
            original.RetryQueueElements.Select(attempt => new QueueHttpTryElement(attempt.StartTimeStamp,
                attempt.IdTransaction, attempt.EndTimeStamp, attempt.HttpCode, attempt.ReservedIp)).ToImmutableArray(),
            original.HttpStatusRetries);

    private static DeadLetterMutationResult ApplyCore(DeadLetterMutation mutation, SlimDataState state)
    {
        string function = mutation.Function;
        string queueKey = "Queue:" + function;
        long now = mutation.NowTicks;
        switch (mutation.Action)
        {
            case DeadLetterAction.Policy:
                var policy = mutation.Policy ?? throw new InvalidDataException("A DLQ policy is required.");
                if (policy.MaxAgeSeconds <= 0 || policy.MaxLength <= 0)
                    throw new InvalidDataException("DLQ retention must be positive.");
                state.KeyValues = state.KeyValues.SetItem(DeadLetterKeys.Policy(function), MemoryPackSerializer.Serialize(policy));
                Increment(state, function, _ => { });
                break;
            case DeadLetterAction.AwaitingCallback:
                if (Queue(state, queueKey).Any(item => item.Id == mutation.ElementId))
                {
                    var active = Active(state, function, mutation.ElementId);
                    active.AwaitingCallback = true;
                    Put(state, DeadLetterKeys.Active(function), mutation.ElementId, MemoryPackSerializer.Serialize(active));
                }
                break;
            case DeadLetterAction.Complete:
                var metadata = Active(state, function, mutation.ElementId);
                metadata.Source = mutation.Source;
                Put(state, DeadLetterKeys.Active(function), mutation.ElementId, MemoryPackSerializer.Serialize(metadata));
                SlimDataInterpreter.DoListCallbackAsync(new ListCallbackCommand
                {
                    Key = queueKey, NowTicks = now,
                    CallbackElements = [new CallbackElement(mutation.ElementId, mutation.HttpStatus)]
                }, state).AsTask().GetAwaiter().GetResult();
                if (!Queue(state, queueKey).Any(item => item.Id == mutation.ElementId))
                    Remove(state, DeadLetterKeys.Active(function), mutation.ElementId);
                break;
            case DeadLetterAction.Sweep:
                SweepQueue(state, queueKey, now);
                Prune(state, function, now);
                break;
            case DeadLetterAction.Delete:
                if (!Get(state, DeadLetterKeys.Messages(function)).TryGetValue(mutation.MessageId, out var deleted) ||
                    MemoryPackSerializer.Deserialize<DeadLetterEntry>(deleted.Span)!.ExpiresAtTicks <= now)
                    return new() { Status = 404 };
                var deletedEntry = MemoryPackSerializer.Deserialize<DeadLetterEntry>(deleted.Span)!;
                RemoveEntry(state, deletedEntry);
                Transition(state, deletedEntry, "dead_letter_deleted");
                return new() { Status = 204 };
            case DeadLetterAction.Requeue:
                if (Get(state, DeadLetterKeys.Receipts(function)).TryGetValue(mutation.MessageId, out var receiptBytes))
                {
                    var receipt = MemoryPackSerializer.Deserialize<DeadLetterReceipt>(receiptBytes.Span)!;
                    return receipt.ExpiresAtTicks > now
                        ? new() { Status = 202, Receipt = receipt }
                        : new() { Status = 404 };
                }
                if (!Get(state, DeadLetterKeys.Messages(function)).TryGetValue(mutation.MessageId, out var entryBytes))
                    return new() { Status = 404 };
                var entry = MemoryPackSerializer.Deserialize<DeadLetterEntry>(entryBytes.Span)!;
                if (entry.ExpiresAtTicks <= now)
                    return new() { Status = 404 };
                if (!Get(state, DeadLetterKeys.Payloads(function)).TryGetValue(entry.Id, out var payload))
                    return new() { Status = 409 };
                var replay = new QueueElement(payload, mutation.NewElementId, now, mutation.RetryTimeoutSeconds,
                    mutation.Retries.ToImmutableArray(), [], mutation.HttpStatusRetries.ToImmutableHashSet());
                var queue = Queue(state, queueKey);
                state.Queues = state.Queues.SetItem(queueKey, queue.IsDefault ? [replay] : queue.Add(replay));
                Put(state, DeadLetterKeys.Active(function), replay.Id, MemoryPackSerializer.Serialize(new DeadLetterActive
                {
                    ReplayOfDeadLetterId = entry.Id,
                    RootElementId = entry.RootElementId ?? entry.OriginalElementId
                }));
                var newReceipt = new DeadLetterReceipt
                {
                    DeadLetterId = entry.Id, OriginalElementId = entry.OriginalElementId,
                    Function = function, ElementId = replay.Id, ExpiresAtTicks = now + TimeSpan.TicksPerDay
                };
                Put(state, DeadLetterKeys.Receipts(function), entry.Id, MemoryPackPayload.Serialize(newReceipt));
                RemoveEntry(state, entry);
                Transition(state, entry, "dead_letter_requeued", replay.Id);
                return new() { Status = 202, Receipt = newReceipt };
            default:
                throw new InvalidDataException("Unknown DLQ action.");
        }
        return new();
    }

    internal static bool ApplyCallbacks(SlimDataState state, string key, IList<CallbackElement> callbacks, long now)
    {
        if (!IsManaged(state, key))
            return false;
        var staged = StageQueue(state, key);
        ApplyCallbacksCore(staged, key, callbacks, now);
        state.Replace(staged.Capture());
        return true;
    }

    private static void ApplyCallbacksCore(SlimDataState state, string key, IList<CallbackElement> callbacks, long now)
    {
        string function = key["Queue:".Length..];
        var queue = Queue(state, key);
        if (queue.IsDefaultOrEmpty)
            return;
        var statuses = callbacks.ToLookup(callback => callback.Identifier, StringComparer.Ordinal);
        var updated = ImmutableArray.CreateBuilder<QueueElement>(queue.Length);
        foreach (var original in queue)
        {
            var element = Clone(original);
            foreach (var callback in statuses[element.Id])
            {
                if (element.RetryQueueElements.IsDefaultOrEmpty)
                    continue;
                var last = element.RetryQueueElements[^1];
                if (last.EndTimeStamp != 0)
                    continue;
                if (callback.HttpCode == 202)
                {
                    var active = Active(state, function, element.Id);
                    active.AwaitingCallback = true;
                    Put(state, DeadLetterKeys.Active(function), element.Id, MemoryPackSerializer.Serialize(active));
                    continue;
                }
                last.EndTimeStamp = now;
                last.HttpCode = callback.HttpCode;
            }
            updated.Add(element);
        }
        state.Queues = state.Queues.SetItem(key, updated.DrainToImmutable());
        SweepQueue(state, key, now);
    }

    internal static void SweepQueue(SlimDataState state, string key, long now)
    {
        if (!key.StartsWith("Queue:", StringComparison.Ordinal))
            return;
        string function = key["Queue:".Length..];
        var policy = Read<DeadLetterPolicy>(state, DeadLetterKeys.Policy(function));
        if (policy is null)
            return;
        var queue = Queue(state, key);
        if (queue.IsDefaultOrEmpty)
            return;
        var kept = ImmutableArray.CreateBuilder<QueueElement>(queue.Length);
        foreach (var original in queue)
        {
            var element = original;
            var active = Active(state, function, element.Id);
            if (element.IsTimeout(now))
            {
                element = Clone(element);
                var timedOutAttempt = element.RetryQueueElements[^1];
                timedOutAttempt.EndTimeStamp = timedOutAttempt.StartTimeStamp + element.HttpTimeoutTicks;
                timedOutAttempt.HttpCode = 504;
                active.Source = active.AwaitingCallback ? "CallbackTimeout" : "DeliveryTimeout";
                Put(state, DeadLetterKeys.Active(function), element.Id, MemoryPackSerializer.Serialize(active));
            }
            bool success = !element.RetryQueueElements.IsDefaultOrEmpty &&
                element.RetryQueueElements[^1].EndTimeStamp > 0 &&
                element.RetryQueueElements[^1].HttpCode is >= 200 and < 300 &&
                element.RetryQueueElements[^1].HttpCode != 202;
            if (!success && !element.IsFinished(now))
            {
                kept.Add(element);
                continue;
            }
            var last = element.RetryQueueElements[^1];
            bool timedOut = active.Source is "DeliveryTimeout" or "CallbackTimeout";
            if (policy.Enabled && !success && last.HttpCode != SlimDataInterpreter.DeleteFromQueueCode)
            {
                string id = "dlq-" + element.Id;
                if (!Get(state, DeadLetterKeys.Messages(function)).ContainsKey(id))
                {
                    long failed = last.EndTimeStamp;
                    string reason = timedOut ? (active.AwaitingCallback ? "CallbackTimeout" : "DeliveryTimeout")
                        : active.Source == "Transport" ? "TransportError"
                        : active.Source == "DeliveryTimeout" ? "DeliveryTimeout"
                        : active.Source == "CallbackTimeout" ? "CallbackTimeout"
                        : active.Source == "Callback" ? "CallbackError"
                        : element.HttpStatusRetries.Contains(last.HttpCode) ? "RetriesExhausted" : "NonRetryableResponse";
                    var entry = new DeadLetterEntry
                    {
                        Id = id, Function = function, OriginalElementId = element.Id,
                        AcceptedAtTicks = element.InsertTimeStamp, FailedAtTicks = failed,
                        ExpiresAtTicks = failed + (long)policy.MaxAgeSeconds * TimeSpan.TicksPerSecond,
                        Attempts = element.NumberOfTries(), LastHttpStatus = timedOut ? null : last.HttpCode,
                        FailureReason = reason,
                        ReplayOfDeadLetterId = active.ReplayOfDeadLetterId,
                        RootElementId = active.RootElementId
                    };
                    Put(state, DeadLetterKeys.Messages(function), id, MemoryPackSerializer.Serialize(entry));
                    state.Hashsets = state.Hashsets.SetItem(DeadLetterKeys.Payloads(function),
                        Get(state, DeadLetterKeys.Payloads(function)).SetItem(id, element.Value));
                    Transition(state, entry, "dead_lettered");
                }

            }
            Remove(state, DeadLetterKeys.Active(function), element.Id);
        }
        state.Queues = state.Queues.SetItem(key, kept.DrainToImmutable());
        Prune(state, function, now);
    }

    internal static void MarkDispatch(SlimDataState state, string key, string id)
    {
        if (!key.StartsWith("Queue:", StringComparison.Ordinal))
            return;
        string function = key["Queue:".Length..];
        if (Read<DeadLetterPolicy>(state, DeadLetterKeys.Policy(function))?.Enabled != true)
            return;
        var active = Active(state, function, id);
        active.AwaitingCallback = false;
        active.Source = "Http";
        Put(state, DeadLetterKeys.Active(function), id, MemoryPackSerializer.Serialize(active));
    }

    private static void Prune(SlimDataState state, string function, long now)
    {
        var policy = Read<DeadLetterPolicy>(state, DeadLetterKeys.Policy(function)) ?? new();
        var entries = Get(state, DeadLetterKeys.Messages(function)).Values
            .Select(bytes => MemoryPackSerializer.Deserialize<DeadLetterEntry>(bytes.Span)!)
            .OrderBy(entry => entry.FailedAtTicks).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToList();
        foreach (var entry in entries.Where(entry => entry.ExpiresAtTicks <= now))
        {
            RemoveEntry(state, entry);
            Transition(state, entry, "dead_letter_expired");
        }
        var live = entries.Where(entry => entry.ExpiresAtTicks > now).ToList();
        foreach (var entry in live.Take(Math.Max(0, live.Count - policy.MaxLength)))
        {
            RemoveEntry(state, entry);
            Transition(state, entry, "dead_letter_evicted");
        }
        foreach (var (id, value) in Get(state, DeadLetterKeys.Receipts(function)))
            if (MemoryPackSerializer.Deserialize<DeadLetterReceipt>(value.Span)!.ExpiresAtTicks <= now)
                Remove(state, DeadLetterKeys.Receipts(function), id);
    }
}
