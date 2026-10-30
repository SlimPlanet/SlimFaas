using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MemoryPack;
using SlimData.Commands;

namespace SlimData;

public enum DurableStreamAction { Configure, Subscribe, Unsubscribe, Append, Reserve, AwaitCallback, Complete, Sweep, Delete }

[MemoryPackable]
public sealed partial class DurableStreamDefinition
{
    public string Name { get; set; } = "";
    public string Generation { get; set; } = "";
    public string Visibility { get; set; } = "Private";
    public int MaxAgeSeconds { get; set; } = 86400;
    public int MaxLength { get; set; } = 100000;
    public long Tail { get; set; }
    public long Earliest { get; set; } = 1;
    public long Revision { get; set; }
}

[MemoryPackable]
public sealed partial class DurableStreamMessage
{
    public string Id { get; set; } = "";
    public long Sequence { get; set; }
    public long AppendedAtTicks { get; set; }
    public string ContentType { get; set; } = "";
    public long BodyLength { get; set; }
}

[MemoryPackable]
public sealed partial class DurableStreamSubscription
{
    public string Function { get; set; } = "";
    public string Generation { get; set; } = "";
    public bool Active { get; set; } = true;
    public long StartSequence { get; set; }
    public long AcknowledgedThrough { get; set; }
    public long LastAcknowledged { get; set; }
    public long RetentionGapCount { get; set; }
    public long NextSequence { get; set; }
    public int? LastStatus { get; set; }
    public string FunctionVisibility { get; set; } = "Private";
    public string ConfigurationFingerprint { get; set; } = "";
}

[MemoryPackable]
public sealed partial class DurableStreamDelivery
{
    public string Stream { get; set; } = "";
    public string Function { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string SubscriptionGeneration { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Replica { get; set; } = "";
    public int Attempt { get; set; }
    public long DeadlineTicks { get; set; }
    public long RetryAtTicks { get; set; }
    public string State { get; set; } = "Running";
    public int LastStatus { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int[] Retries { get; set; } = [];
    public int[] RetryStatuses { get; set; } = [];
}

[MemoryPackable]
public sealed partial class DurableStreamMutation
{
    public string FunctionVisibility { get; set; } = "Private";
    public string ConfigurationFingerprint { get; set; } = "";
    public DurableStreamAction Action { get; set; }
    public string Stream { get; set; } = "";
    public string Function { get; set; } = "";
    public string NewId { get; set; } = "";
    public string ElementId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Replica { get; set; } = "";
    public string Visibility { get; set; } = "Private";
    public int MaxAgeSeconds { get; set; } = 86400;
    public int MaxLength { get; set; } = 100000;
    public long NowTicks { get; set; }
    public byte[] Value { get; set; } = [];
    public string ContentType { get; set; } = "";
    public long BodyLength { get; set; }
    public int StatusCode { get; set; }
    public int MaximumParallel { get; set; } = 10;
    public int MaximumPerReplica { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 120;
    public int[] Retries { get; set; } = [];
    public int[] RetryStatuses { get; set; } = [];
}

[MemoryPackable]
public sealed partial class DurableStreamMutationResult
{
    public int Status { get; set; } = 200;
    public string? Error { get; set; }
    public string? MessageId { get; set; }
    public DurableStreamDelivery? Delivery { get; set; }
    public bool Unchanged { get; set; }
}

public sealed record DurableStreamCounters(long Lag, long PendingCount, long WaitingForCallbackCount,
    long RetryScheduledCount, long FailedCount, long RunnableCount);

public static class DurableStreamStore
{
    public const string DefinitionPrefix = "\0stream:definition:";
    private const string AttemptKey = "\0stream:attempts";
    private const string ActiveAttemptKey = "\0stream:active-attempts";
    private static string CapacityKey(string function) => "\0stream:capacity:" + function;
    private static string DefinitionKey(string name) => DefinitionPrefix + name;
    private static string MessagesKey(string name) => "\0stream:messages:" + name;
    private static string BodiesKey(string name) => "\0stream:bodies:" + name;
    private static string SubscriptionsKey(string name) => "\0stream:subscriptions:" + name;
    private static string DeliveriesKey(string name, string function) => "\0stream:deliveries:" + name + ":" + function;
    private static string PendingKey(string name, string function) => "\0stream:pending:" + name + ":" + function;
    private static string AcksKey(string name, string generation) => "\0stream:acks:" + name + ":" + generation;
    private static string Id(DurableStreamDefinition definition, long sequence) =>
        definition.Generation + "_" + sequence.ToString("D20", CultureInfo.InvariantCulture);

    private static T? Decode<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(ReadOnlyMemory<byte> bytes) where T : class =>
        bytes.IsEmpty ? null : MemoryPackSerializer.Deserialize<T>(bytes.Span);
    private static ImmutableDictionary<string, ReadOnlyMemory<byte>> Hash(SlimDataPayload payload, string key) =>
        payload.Hashsets.GetValueOrDefault(key) ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
    private static void Put(SlimDataState state, string key, string field, byte[] bytes) =>
        state.Hashsets = state.Hashsets.SetItem(key, Hash(state.CapturePayload(), key).SetItem(field, bytes));
    private static void Remove(SlimDataState state, string key, string field) =>
        state.Hashsets = state.Hashsets.SetItem(key, Hash(state.CapturePayload(), key).Remove(field));
    private static void Save(SlimDataState state, DurableStreamDefinition definition)
    {
        definition.Revision++;
        state.KeyValues = state.KeyValues.SetItem(DefinitionKey(definition.Name), MemoryPackPayload.Serialize(definition));
    }

    public static bool IsSafeName(string? name) => !string.IsNullOrEmpty(name) && name is not ("." or "..") && name.Length <= 256 &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    public static IEnumerable<DurableStreamDefinition> Definitions(SlimDataPayload payload) =>
        payload.KeyValues.Where(item => item.Key.StartsWith(DefinitionPrefix, StringComparison.Ordinal))
            .Select(item => Decode<DurableStreamDefinition>(item.Value)!);
    public static DurableStreamDefinition? Definition(SlimDataPayload payload, string name) =>
        Decode<DurableStreamDefinition>(payload.KeyValues.GetValueOrDefault(DefinitionKey(name)));
    public static IEnumerable<DurableStreamSubscription> Subscriptions(SlimDataPayload payload, string name) =>
        Hash(payload, SubscriptionsKey(name)).Values.Select(Decode<DurableStreamSubscription>).OfType<DurableStreamSubscription>();
    public static DurableStreamSubscription? Subscription(SlimDataPayload payload, string name, string function) =>
        Decode<DurableStreamSubscription>(Hash(payload, SubscriptionsKey(name)).GetValueOrDefault(function));
    public static DurableStreamMessage? Message(SlimDataPayload payload, string name, string id) =>
        Decode<DurableStreamMessage>(Hash(payload, MessagesKey(name)).GetValueOrDefault(id));
    public static ReadOnlyMemory<byte> Body(SlimDataPayload payload, string name, string id) =>
        Hash(payload, BodiesKey(name)).GetValueOrDefault(id);
    public static DurableStreamDelivery? Attempt(SlimDataPayload payload, string elementId) =>
        Decode<DurableStreamDelivery>(Hash(payload, AttemptKey).GetValueOrDefault(elementId));
    public static IEnumerable<DurableStreamDelivery> Deliveries(SlimDataPayload payload, string name, string function) =>
        Hash(payload, PendingKey(name, function)).Values.Select(Decode<DurableStreamDelivery>).OfType<DurableStreamDelivery>();
    private static IEnumerable<DurableStreamDelivery> AllDeliveries(SlimDataPayload payload, string name, string function) =>
        Hash(payload, DeliveriesKey(name, function)).Values.Select(Decode<DurableStreamDelivery>).OfType<DurableStreamDelivery>();
    public static IEnumerable<DurableStreamMessage> Messages(SlimDataPayload payload, DurableStreamDefinition definition, long after, int limit)
    {
        long start = Math.Max(definition.Earliest, after + 1);
        for (long sequence = start; sequence <= definition.Tail && limit > 0; sequence++, limit--)
        {
            var message = Message(payload, definition.Name, Id(definition, sequence));
            if (message is not null)
                yield return message;
        }
    }

    public static DurableStreamCounters Counters(SlimDataPayload payload, DurableStreamDefinition definition, DurableStreamSubscription subscription)
    {
        long retained = Math.Max(0, definition.Tail - Math.Max(subscription.StartSequence, definition.Earliest - 1));
        long acknowledged = Hash(payload, AcksKey(definition.Name, subscription.Generation)).Count;
        var deliveries = Deliveries(payload, definition.Name, subscription.Function)
            .Where(item => item.SubscriptionGeneration == subscription.Generation &&
                Message(payload, definition.Name, item.MessageId) is not null).ToArray();
        long failed = deliveries.Count(item => item.State == "Failed");
        long lag = Math.Max(0, retained - acknowledged);
        return new(lag, deliveries.Length, deliveries.Count(item => item.State == "WaitingForCallback"),
            deliveries.Count(item => item.State == "RetryScheduled"), failed, Math.Max(0, lag - failed));
    }

    public static int Running(SlimDataPayload payload, string function, string? replica = null) =>
        Hash(payload, ActiveAttemptKey).Values.Select(Decode<DurableStreamDelivery>)
            .Count(item => item is not null && item.Function == function &&
                item.State is "Running" or "WaitingForCallback" && (replica is null || item.Replica == replica));

    public static (int Available, int PerReplica)? QueueCapacity(SlimDataPayload payload, string function, long now)
    {
        if (!payload.KeyValues.TryGetValue(CapacityKey(function), out var bytes))
            return null;
        int maximum = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span);
        int perReplica = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[4..]);
        int running = Running(payload, function);
        if (running == 0 && !Definitions(payload).Any(definition =>
                Subscription(payload, definition.Name, function)?.Active == true))
            return null;
        return (Math.Max(0, maximum - running - QueueRunning(payload, function, now)), perReplica);
    }

    public static void Validate(DurableStreamMutation mutation)
    {
        if (!Enum.IsDefined(mutation.Action) || !IsSafeName(mutation.Stream) ||
            mutation.NowTicks <= 0 || mutation.NowTicks > DateTime.MaxValue.Ticks ||
            mutation.MaxAgeSeconds <= 0 || mutation.MaxLength <= 0 ||
            mutation.Visibility is not ("Private" or "Public") || mutation.Value is null ||
            mutation.Value.Length > 15 * 1024 * 1024)
            throw new InvalidDataException("Invalid Stream mutation.");
        if (mutation.Action is DurableStreamAction.Subscribe or DurableStreamAction.Unsubscribe or DurableStreamAction.Reserve &&
            !IsSafeName(mutation.Function))
            throw new InvalidDataException("Invalid Stream function.");
        if (mutation.Action is DurableStreamAction.Configure or DurableStreamAction.Subscribe or DurableStreamAction.Reserve &&
            !IsSafeName(mutation.NewId))
            throw new InvalidDataException("Missing Stream generation or attempt identity.");
        if (mutation.Action == DurableStreamAction.Reserve &&
            (mutation.MaximumParallel <= 0 || mutation.MaximumPerReplica <= 0 || mutation.TimeoutSeconds <= 0 ||
             string.IsNullOrEmpty(mutation.Owner) || string.IsNullOrEmpty(mutation.Replica) ||
             mutation.Retries is null || mutation.RetryStatuses is null || mutation.Retries.Any(value => value < 0)))
            throw new InvalidDataException("Invalid Stream reservation.");
    }

    public static DurableStreamMutationResult Apply(DurableStreamMutation mutation, SlimDataState state)
    {
        Validate(mutation);
        var payload = state.CapturePayload();
        var definition = Definition(payload, mutation.Stream);
        if (mutation.Action == DurableStreamAction.Configure)
        {
            if (definition is not null)
                return definition.Visibility == mutation.Visibility && definition.MaxAgeSeconds == mutation.MaxAgeSeconds &&
                    definition.MaxLength == mutation.MaxLength ? new() : Error(409, "Conflicting Stream configuration.");
            definition = new()
            {
                Name = mutation.Stream, Generation = mutation.NewId, Visibility = mutation.Visibility,
                MaxAgeSeconds = mutation.MaxAgeSeconds, MaxLength = mutation.MaxLength
            };
            Save(state, definition);
            return new() { Status = 201 };
        }
        if (definition is null)
            return Error(404, "Stream not found.");
        if (mutation.Action == DurableStreamAction.Delete)
        {
            if (Subscriptions(payload, mutation.Stream).Any(subscription =>
                Deliveries(payload, mutation.Stream, subscription.Function).Any(item => item.State is "Running" or "WaitingForCallback")))
                return Error(409, "Stream has active deliveries.");
            foreach (var subscription in Subscriptions(payload, mutation.Stream))
            {
                foreach (var delivery in AllDeliveries(payload, mutation.Stream, subscription.Function))
                {
                    Remove(state, AttemptKey, delivery.ElementId);
                    Remove(state, ActiveAttemptKey, delivery.ElementId);
                }
                state.Hashsets = state.Hashsets.Remove(DeliveriesKey(mutation.Stream, subscription.Function))
                    .Remove(PendingKey(mutation.Stream, subscription.Function))
                    .Remove(AcksKey(mutation.Stream, subscription.Generation));
            }
            state.Hashsets = state.Hashsets.Remove(MessagesKey(mutation.Stream)).Remove(BodiesKey(mutation.Stream))
                .Remove(SubscriptionsKey(mutation.Stream));
            state.KeyValues = state.KeyValues.Remove(DefinitionKey(mutation.Stream));
            return new() { Status = 204 };
        }
        if (mutation.Action is DurableStreamAction.Subscribe or DurableStreamAction.Unsubscribe)
        {
            var subscription = Subscription(payload, mutation.Stream, mutation.Function);
            if (mutation.Action == DurableStreamAction.Unsubscribe)
            {
                if (subscription is null || !subscription.Active)
                    return new();
                subscription.Active = false;
            }
            else
            {
                if (Definitions(payload).Any(stream =>
                        Subscription(payload, stream.Name, mutation.Function) is { Active: true } active &&
                        active.ConfigurationFingerprint != mutation.ConfigurationFingerprint))
                    return Error(409, "Conflicting durable function registration.");
                if (subscription is { Active: false } &&
                    Deliveries(payload, mutation.Stream, mutation.Function).Any(item => item.State is "Running" or "WaitingForCallback"))
                    return Error(409, "Previous subscription still has active deliveries.");
                var capacity = new byte[8];
                BinaryPrimitives.WriteInt32LittleEndian(capacity, mutation.MaximumParallel);
                BinaryPrimitives.WriteInt32LittleEndian(capacity.AsSpan(4), mutation.MaximumPerReplica);
                state.KeyValues = state.KeyValues.SetItem(CapacityKey(mutation.Function), capacity);
                if (subscription?.Active == true)
                {
                    if (subscription.FunctionVisibility != mutation.FunctionVisibility)
                    {
                        subscription.FunctionVisibility = mutation.FunctionVisibility;
                        Put(state, SubscriptionsKey(mutation.Stream), subscription.Function, MemoryPackPayload.Serialize(subscription));
                        Save(state, definition);
                    }
                    return new();
                }
                if (subscription is not null)
                {
                    foreach (var old in AllDeliveries(payload, mutation.Stream, mutation.Function))
                        Remove(state, AttemptKey, old.ElementId);
                    state.Hashsets = state.Hashsets.Remove(AcksKey(mutation.Stream, subscription.Generation))
                        .Remove(PendingKey(mutation.Stream, mutation.Function))
                        .Remove(DeliveriesKey(mutation.Stream, mutation.Function));
                }
                subscription = new()
                {
                    Function = mutation.Function, Generation = mutation.NewId, StartSequence = definition.Tail,
                    AcknowledgedThrough = definition.Tail, NextSequence = definition.Tail + 1,
                    FunctionVisibility = mutation.FunctionVisibility, ConfigurationFingerprint = mutation.ConfigurationFingerprint
                };
            }
            Put(state, SubscriptionsKey(mutation.Stream), mutation.Function, MemoryPackPayload.Serialize(subscription));
            Save(state, definition);
            return new();
        }
        if (mutation.Action == DurableStreamAction.Append)
        {
            long sequence = checked(definition.Tail + 1);
            var message = new DurableStreamMessage
            {
                Id = Id(definition, sequence), Sequence = sequence, AppendedAtTicks = mutation.NowTicks,
                ContentType = mutation.ContentType, BodyLength = mutation.BodyLength
            };
            Put(state, MessagesKey(mutation.Stream), message.Id, MemoryPackPayload.Serialize(message));
            Put(state, BodiesKey(mutation.Stream), message.Id, mutation.Value);
            definition.Tail = sequence;
            Sweep(state, definition, mutation.NowTicks);
            Save(state, definition);
            return new() { Status = 201, MessageId = message.Id };
        }
        if (mutation.Action == DurableStreamAction.Sweep)
        {
            if (Sweep(state, definition, mutation.NowTicks))
                Save(state, definition);
            return new();
        }
        if (mutation.Action == DurableStreamAction.Reserve)
        {
            if (Sweep(state, definition, mutation.NowTicks))
                Save(state, definition);
            payload = state.CapturePayload();
            var subscription = Subscription(payload, mutation.Stream, mutation.Function);
            if (subscription?.Active != true)
                return Error(404, "Subscription not found.");
            int queueRunning = QueueRunning(payload, mutation.Function, mutation.NowTicks);
            int replicaRunning = QueueRunning(payload, mutation.Function, mutation.NowTicks, mutation.Replica);
            if (Running(payload, mutation.Function) + queueRunning >= mutation.MaximumParallel ||
                Running(payload, mutation.Function, mutation.Replica) + replicaRunning >= mutation.MaximumPerReplica)
                return new() { Status = 204 };
            var due = Deliveries(payload, mutation.Stream, mutation.Function)
                .Where(item => item.State == "RetryScheduled" && item.RetryAtTicks <= mutation.NowTicks)
                .OrderBy(item => item.MessageId, StringComparer.Ordinal).FirstOrDefault();
            long next = Math.Max(definition.Earliest, subscription.NextSequence);
            if (due is not null || next <= definition.Tail)
            {
                string id = due?.MessageId ?? Id(definition, next);
                var previous = due;
                var delivery = new DurableStreamDelivery
                {
                    Stream = mutation.Stream, Function = mutation.Function, MessageId = id,
                    SubscriptionGeneration = subscription.Generation, ElementId = mutation.NewId,
                    Owner = mutation.Owner, Replica = mutation.Replica, Attempt = (previous?.Attempt ?? 0) + 1,
                    DeadlineTicks = Deadline(mutation.NowTicks, mutation.TimeoutSeconds),
                    TimeoutSeconds = mutation.TimeoutSeconds, Retries = mutation.Retries, RetryStatuses = mutation.RetryStatuses
                };
                if (previous is not null)
                    Remove(state, AttemptKey, previous.ElementId);
                else
                {
                    subscription.NextSequence = next + 1;
                    Put(state, SubscriptionsKey(mutation.Stream), subscription.Function, MemoryPackPayload.Serialize(subscription));
                }
                SaveDelivery(state, delivery);
                Save(state, definition);
                return new() { Delivery = delivery, MessageId = id };
            }
            return new() { Status = 204 };
        }
        var attempt = Attempt(state.CapturePayload(), mutation.ElementId);
        if (attempt is null || attempt.Stream != mutation.Stream ||
            (!string.IsNullOrEmpty(mutation.Function) && attempt.Function != mutation.Function))
            return Error(404, "Delivery not found.");
        var current = Subscription(state.CapturePayload(), mutation.Stream, attempt.Function);
        if (current?.Generation != attempt.SubscriptionGeneration)
            return Error(409, "Stale subscription.");
        if (attempt.State == "Acknowledged")
            return new() { Unchanged = true };
        if (Message(state.CapturePayload(), mutation.Stream, attempt.MessageId) is null)
        {
            attempt.State = "Expired";
            SaveDelivery(state, attempt);
            Save(state, definition);
            return Error(410, "Message left retention.");
        }
        if (attempt.State is not ("Running" or "WaitingForCallback"))
            return Error(409, "Delivery is no longer active.");
        if (attempt.DeadlineTicks <= mutation.NowTicks)
        {
            Fail(state, attempt, 504, mutation.NowTicks);
            Save(state, definition);
            return Error(409, "Delivery lease expired.");
        }
        if (mutation.Action == DurableStreamAction.AwaitCallback || mutation.StatusCode == 202)
        {
            if (attempt.State == "WaitingForCallback")
                return new() { Unchanged = true };
            attempt.State = "WaitingForCallback";
            SaveDelivery(state, attempt);
        }
        else if (mutation.Action == DurableStreamAction.Complete && mutation.StatusCode is >= 200 and < 300)
        {
            attempt.State = "Acknowledged";
            attempt.LastStatus = mutation.StatusCode;
            SaveDelivery(state, attempt);
            Put(state, AcksKey(mutation.Stream, current.Generation), attempt.MessageId, [1]);
            var message = Message(state.CapturePayload(), mutation.Stream, attempt.MessageId);
            if (message is not null)
                current.LastAcknowledged = Math.Max(current.LastAcknowledged, message.Sequence);
            var acks = Hash(state.CapturePayload(), AcksKey(mutation.Stream, current.Generation));
            while (current.AcknowledgedThrough < definition.Tail && acks.ContainsKey(Id(definition, current.AcknowledgedThrough + 1)))
                current.AcknowledgedThrough++;
            Put(state, SubscriptionsKey(mutation.Stream), current.Function, MemoryPackPayload.Serialize(current));
        }
        else
            Fail(state, attempt, mutation.StatusCode, mutation.NowTicks);
        current.LastStatus = mutation.StatusCode;
        Put(state, SubscriptionsKey(mutation.Stream), current.Function, MemoryPackPayload.Serialize(current));
        Save(state, definition);
        return new();
    }

    private static long Deadline(long now, int seconds) =>
        now > DateTime.MaxValue.Ticks - seconds * TimeSpan.TicksPerSecond ? DateTime.MaxValue.Ticks : now + seconds * TimeSpan.TicksPerSecond;
    private static DurableStreamMutationResult Error(int status, string message) => new() { Status = status, Error = message };
    private static void SaveDelivery(SlimDataState state, DurableStreamDelivery delivery)
    {
        byte[] value = MemoryPackPayload.Serialize(delivery);
        Put(state, DeliveriesKey(delivery.Stream, delivery.Function), delivery.MessageId, value);
        Put(state, AttemptKey, delivery.ElementId, value);
        if (delivery.State is "Running" or "WaitingForCallback")
            Put(state, ActiveAttemptKey, delivery.ElementId, value);
        else
            Remove(state, ActiveAttemptKey, delivery.ElementId);
        if (delivery.State == "Acknowledged")
            Remove(state, PendingKey(delivery.Stream, delivery.Function), delivery.MessageId);
        else
            Put(state, PendingKey(delivery.Stream, delivery.Function), delivery.MessageId, value);
    }
    private static void Fail(SlimDataState state, DurableStreamDelivery delivery, int status, long now)
    {
        delivery.LastStatus = status;
        bool retry = (status == 504 || delivery.RetryStatuses.Contains(status)) && delivery.Attempt <= delivery.Retries.Length;
        delivery.State = retry ? "RetryScheduled" : "Failed";
        delivery.RetryAtTicks = retry ? Deadline(now, delivery.Retries[delivery.Attempt - 1]) : 0;
        SaveDelivery(state, delivery);
    }
    private static bool Sweep(SlimDataState state, DurableStreamDefinition definition, long now)
    {
        bool changed = false;
        foreach (var subscription in Subscriptions(state.CapturePayload(), definition.Name).ToArray())
        {
            foreach (var delivery in Deliveries(state.CapturePayload(), definition.Name, subscription.Function).ToArray())
            {
                if (delivery.State is "Running" or "WaitingForCallback" && delivery.DeadlineTicks <= now)
                {
                    Fail(state, delivery, 504, now);
                    changed = true;
                }
                if (delivery.State is not ("Running" or "WaitingForCallback") &&
                    Message(state.CapturePayload(), definition.Name, delivery.MessageId) is null)
                {
                    Remove(state, AttemptKey, delivery.ElementId);
                    Remove(state, PendingKey(definition.Name, subscription.Function), delivery.MessageId);
                    Remove(state, DeliveriesKey(definition.Name, subscription.Function), delivery.MessageId);
                    changed = true;
                }
            }
        }
        while (definition.Earliest <= definition.Tail)
        {
            string id = Id(definition, definition.Earliest);
            var message = Message(state.CapturePayload(), definition.Name, id)!;
            if (definition.Tail - definition.Earliest < definition.MaxLength &&
                message.AppendedAtTicks > now - definition.MaxAgeSeconds * TimeSpan.TicksPerSecond)
                break;
            foreach (var subscription in Subscriptions(state.CapturePayload(), definition.Name).ToArray())
            {
                var acks = Hash(state.CapturePayload(), AcksKey(definition.Name, subscription.Generation));
                if (message.Sequence > subscription.StartSequence && !acks.ContainsKey(id))
                {
                    subscription.RetentionGapCount++;
                    Put(state, SubscriptionsKey(definition.Name), subscription.Function, MemoryPackPayload.Serialize(subscription));
                }
                Remove(state, AcksKey(definition.Name, subscription.Generation), id);
                var delivery = Decode<DurableStreamDelivery>(Hash(state.CapturePayload(), DeliveriesKey(definition.Name, subscription.Function)).GetValueOrDefault(id));
                if (delivery is not null && delivery.State is not ("Running" or "WaitingForCallback"))
                {
                    Remove(state, AttemptKey, delivery.ElementId);
                    Remove(state, PendingKey(definition.Name, subscription.Function), id);
                    Remove(state, DeliveriesKey(definition.Name, subscription.Function), id);
                }
            }
            Remove(state, MessagesKey(definition.Name), id);
            Remove(state, BodiesKey(definition.Name), id);
            definition.Earliest++;
            changed = true;
        }
        return changed;
    }

    private static int QueueRunning(SlimDataPayload payload, string function, long now, string? replica = null) =>
        payload.Queues.TryGetValue("Queue:" + function, out var queue)
            ? queue.Count(item => item.RetryQueueElements.Length > 0 &&
                item.RetryQueueElements[^1].EndTimeStamp == 0 &&
                item.RetryQueueElements[^1].StartTimeStamp + item.HttpTimeoutSeconds * TimeSpan.TicksPerSecond > now &&
                (replica is null || item.RetryQueueElements[^1].ReservedIp == replica))
            : 0;
}
