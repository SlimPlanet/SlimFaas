using System.Collections.Immutable;
using MemoryPack;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class DurableStreamTests
{
    private static readonly long Now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static SlimDataState Create() => new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
    private static DurableStreamMutationResult Apply(SlimDataState state, DurableStreamAction action,
        string function = "billing", string element = "", int status = 200, long elapsed = 0, int length = 100000) =>
        DurableStreamStore.Apply(new()
        {
            Action = action, Stream = "orders", Function = function, NewId = Guid.NewGuid().ToString("N"),
            ElementId = element, StatusCode = status, NowTicks = Now + elapsed * TimeSpan.TicksPerSecond,
            MaxLength = length, Value = [1, 2, 3], BodyLength = 3, Owner = "node1", Replica = "replica1",
            MaximumParallel = 2, MaximumPerReplica = 2, TimeoutSeconds = 10, Retries = [1], RetryStatuses = [503]
        }, state);
    private static DurableStreamCounters Counts(SlimDataState state, string function = "billing")
    {
        var payload = state.CapturePayload();
        return DurableStreamStore.Counters(payload, DurableStreamStore.Definition(payload, "orders")!,
            DurableStreamStore.Subscription(payload, "orders", function)!);
    }

    [Fact]
    public void Rejected_resubscription_preserves_the_capacity_of_in_flight_work()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append); Apply(state, DurableStreamAction.Reserve);
        Apply(state, DurableStreamAction.Unsubscribe);
        Assert.Equal(409, DurableStreamStore.Apply(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing", NewId = "replacement",
            MaximumParallel = 100, MaximumPerReplica = 100, NowTicks = Now
        }, state).Status);
        var capacity = DurableStreamStore.QueueCapacity(state.CapturePayload(), "billing", Now)!.Value;
        Assert.Equal(1, capacity.Available);
        Assert.Equal(2, capacity.PerReplica);
    }

    [Fact]
    public void Conflicting_function_registrations_are_fenced_even_with_disjoint_Stream_lists()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure);
        var mutation = new DurableStreamMutation
        {
            Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing", NewId = "subscription",
            ConfigurationFingerprint = "configuration-a", NowTicks = Now
        };
        Assert.Equal(200, DurableStreamStore.Apply(mutation, state).Status);
        mutation.Action = DurableStreamAction.Configure; mutation.Stream = "notifications";
        Assert.Equal(201, DurableStreamStore.Apply(mutation, state).Status);
        mutation.Action = DurableStreamAction.Subscribe; mutation.ConfigurationFingerprint = "configuration-b";
        Assert.Equal(409, DurableStreamStore.Apply(mutation, state).Status);
        Assert.Null(DurableStreamStore.Subscription(state.CapturePayload(), "notifications", "billing"));
        Assert.True(DurableStreamStore.Subscription(state.CapturePayload(), "orders", "billing")!.Active);
        mutation.ConfigurationFingerprint = "configuration-a";
        Assert.Equal(200, DurableStreamStore.Apply(mutation, state).Status);
    }

    [Fact]
    public void Aot_payload_encoding_preserves_the_generated_MemoryPack_wire_format()
    {
        var definition = new DurableStreamDefinition { Name = "orders", Generation = "generation" };
        var subscription = new DurableStreamSubscription { Function = "billing", Generation = "subscription" };
        var message = new DurableStreamMessage { Id = "generation_1", BodyLength = 100 };
        var delivery = new DurableStreamDelivery { ElementId = "stream_attempt", MessageId = message.Id, Retries = [1, 2] };
        Assert.Equal(MemoryPackSerializer.Serialize(definition), MemoryPackPayload.Serialize(definition));
        Assert.Equal(MemoryPackSerializer.Serialize(subscription), MemoryPackPayload.Serialize(subscription));
        Assert.Equal(MemoryPackSerializer.Serialize(message), MemoryPackPayload.Serialize(message));
        Assert.Equal(MemoryPackSerializer.Serialize(delivery), MemoryPackPayload.Serialize(delivery));
    }

    [Fact]
    public async Task Queue_and_Stream_reservations_share_global_and_per_replica_capacity()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure);
        DurableStreamStore.Apply(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing", NewId = "subscription",
            MaximumParallel = 2, MaximumPerReplica = 1, NowTicks = Now
        }, state);
        state.Queues = state.Queues.SetItem("Queue:billing",
        [
            new(new byte[] { 1 }, "running", Now, 10, [], [new(Now, "existing", reservedIp: "replica1")], []),
            new(new byte[] { 2 }, "available", Now, 10, [], [], [])
        ]);
        Apply(state, DurableStreamAction.Append);
        var mutation = new DurableStreamMutation
        {
            Action = DurableStreamAction.Reserve, Stream = "orders", Function = "billing", NewId = "delivery",
            Owner = "node1", Replica = "replica1", MaximumParallel = 2, MaximumPerReplica = 1,
            NowTicks = Now
        };
        Assert.Equal(204, DurableStreamStore.Apply(mutation, state).Status);
        mutation.Replica = "replica2";
        var delivery = DurableStreamStore.Apply(mutation, state).Delivery!;
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 2, NowTicks = Now, IdTransaction = "blocked", ReservedIps = ["replica3", "replica4"]
        }, state);
        Assert.Empty(state.Queues["Queue:billing"].Single(item => item.Id == "available").RetryQueueElements);
        Apply(state, DurableStreamAction.Complete, element: delivery.ElementId);
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 2, NowTicks = Now, IdTransaction = "accepted", ReservedIps = ["replica1", "replica2"]
        }, state);
        var queued = state.Queues["Queue:billing"].Single(item => item.Id == "available");
        Assert.Equal("replica2", Assert.Single(queued.RetryQueueElements).ReservedIp);
        Apply(state, DurableStreamAction.Append);
        mutation.NewId = "blocked-delivery";
        Assert.Equal(204, DurableStreamStore.Apply(mutation, state).Status);
    }

    [Fact]
    public void Reconciliation_updates_function_visibility_without_resetting_progress()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        string generation = DurableStreamStore.Subscription(state.CapturePayload(), "orders", "billing")!.Generation;
        Apply(state, DurableStreamAction.Append);
        DurableStreamStore.Apply(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing", NewId = "unused",
            FunctionVisibility = "Public", NowTicks = Now
        }, state);
        var subscription = DurableStreamStore.Subscription(state.CapturePayload(), "orders", "billing")!;
        Assert.Equal("Public", subscription.FunctionVisibility);
        Assert.Equal(generation, subscription.Generation);
        Assert.Equal(1, Counts(state).Lag);
    }

    [Fact]
    public void Unsubscribed_functions_release_queue_policy_only_after_outstanding_work_finishes()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append);
        var delivery = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Apply(state, DurableStreamAction.Unsubscribe);
        Assert.Equal(1, DurableStreamStore.QueueCapacity(state.CapturePayload(), "billing", Now)!.Value.Available);
        Apply(state, DurableStreamAction.Complete, element: delivery.ElementId);
        Assert.Null(DurableStreamStore.QueueCapacity(state.CapturePayload(), "billing", Now));
    }

    [Fact]
    public void New_subscriptions_start_at_tail_and_functions_ack_independently()
    {
        var state = Create();
        Apply(state, DurableStreamAction.Configure);
        string historical = Apply(state, DurableStreamAction.Append).MessageId!;
        Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Subscribe, "email");
        string current = Apply(state, DurableStreamAction.Append).MessageId!;
        var first = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Assert.Equal(current, first.MessageId);
        Assert.NotEqual(historical, current);
        Apply(state, DurableStreamAction.Complete, element: first.ElementId);
        Assert.Equal(0, Counts(state).Lag);
        Assert.Equal(1, Counts(state, "email").Lag);
        Assert.Equal(2, DurableStreamStore.Definition(state.CapturePayload(), "orders")!.Tail);
        Assert.Equal(new byte[] { 1, 2, 3 }, DurableStreamStore.Body(state.CapturePayload(), "orders", current).ToArray());
    }

    [Fact]
    public void Callback_waits_consume_capacity_and_failures_are_never_acknowledgements()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append);
        var first = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Apply(state, DurableStreamAction.Complete, element: first.ElementId, status: 202);
        Assert.Equal(1, Counts(state).WaitingForCallbackCount);
        Assert.Equal(409, Apply(state, DurableStreamAction.Delete).Status);
        Apply(state, DurableStreamAction.Complete, element: first.ElementId, status: 400);
        Assert.Equal(1, Counts(state).FailedCount);
        Assert.Equal(1, Counts(state).Lag);
        Assert.Equal(0, Counts(state).RunnableCount);
        Assert.Equal(409, Apply(state, DurableStreamAction.Complete, element: first.ElementId).Status);
    }

    [Fact]
    public void Out_of_order_success_does_not_skip_unacknowledged_work()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append); Apply(state, DurableStreamAction.Append);
        var first = Apply(state, DurableStreamAction.Reserve).Delivery!;
        var second = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Apply(state, DurableStreamAction.Complete, element: second.ElementId);
        var subscription = DurableStreamStore.Subscription(state.CapturePayload(), "orders", "billing")!;
        Assert.Equal(0, subscription.AcknowledgedThrough);
        Assert.Equal(2, subscription.LastAcknowledged);
        Apply(state, DurableStreamAction.Complete, element: first.ElementId);
        Assert.Equal(2, DurableStreamStore.Subscription(state.CapturePayload(), "orders", "billing")!.AcknowledgedThrough);
        Assert.Equal(0, Counts(state).Lag);
    }

    [Fact]
    public void Retention_reports_loss_without_success_and_resubscribe_skips_old_work()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure, length: 1); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append); Apply(state, DurableStreamAction.Append);
        Assert.Equal(1, DurableStreamStore.Subscription(state.CapturePayload(), "orders", "billing")!.RetentionGapCount);
        Assert.Equal(1, Counts(state).Lag);
        Apply(state, DurableStreamAction.Unsubscribe); Apply(state, DurableStreamAction.Subscribe);
        Assert.Equal(0, Counts(state).Lag);
        Assert.Equal(204, Apply(state, DurableStreamAction.Reserve).Status);
    }

    [Fact]
    public void Retry_reclaim_fences_old_attempt_and_snapshot_observation_is_read_only()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append);
        var first = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Apply(state, DurableStreamAction.Sweep, elapsed: 11);
        Assert.Equal(1, Counts(state).RetryScheduledCount);
        var next = Apply(state, DurableStreamAction.Reserve, elapsed: 12).Delivery!;
        Assert.Equal(2, next.Attempt);
        Assert.Equal(404, Apply(state, DurableStreamAction.Complete, element: first.ElementId, elapsed: 12).Status);
        var before = state.Capture();
        var payload = state.CapturePayload();
        _ = DurableStreamStore.Messages(payload, DurableStreamStore.Definition(payload, "orders")!, 0, 100).ToArray();
        _ = Counts(state);
        Assert.Equal(before, state.Capture());
        Assert.Equal(1, Counts(state).PendingCount);
    }

    [Fact]
    public void Conflicting_policy_is_rejected_without_changing_committed_configuration()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure);
        Assert.Equal(409, Apply(state, DurableStreamAction.Configure, length: 1).Status);
        Assert.Equal(100000, DurableStreamStore.Definition(state.CapturePayload(), "orders")!.MaxLength);
    }

    [Fact]
    public void Expired_active_attempts_release_capacity_without_fabricating_an_ack()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure, length: 1); Apply(state, DurableStreamAction.Subscribe);
        Apply(state, DurableStreamAction.Append);
        var first = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Apply(state, DurableStreamAction.Append);
        Assert.Equal(1, DurableStreamStore.Running(state.CapturePayload(), "billing"));
        Assert.Equal(0, Counts(state).PendingCount);
        Assert.Equal(410, Apply(state, DurableStreamAction.Complete, element: first.ElementId).Status);
        Assert.Equal(0, DurableStreamStore.Running(state.CapturePayload(), "billing"));
        Apply(state, DurableStreamAction.Sweep, elapsed: 11);
        Assert.Null(DurableStreamStore.Attempt(state.CapturePayload(), first.ElementId));
        Assert.Equal(1, Counts(state).Lag);
    }

    [Fact]
    public void Failed_predecessors_do_not_block_later_messages_or_reopen_them_on_retry()
    {
        var state = Create(); Apply(state, DurableStreamAction.Configure); Apply(state, DurableStreamAction.Subscribe);
        for (int i = 0; i < 10; i++) Apply(state, DurableStreamAction.Append);
        var first = Apply(state, DurableStreamAction.Reserve).Delivery!;
        Apply(state, DurableStreamAction.Complete, element: first.ElementId, status: 400);
        for (int i = 1; i < 10; i++)
        {
            var delivery = Apply(state, DurableStreamAction.Reserve).Delivery!;
            Assert.NotEqual(first.MessageId, delivery.MessageId);
            Apply(state, DurableStreamAction.Complete, element: delivery.ElementId);
        }
        Assert.Equal(1, Counts(state).Lag); Assert.Equal(0, Counts(state).RunnableCount);
        Assert.Equal(204, Apply(state, DurableStreamAction.Reserve).Status);
        Assert.Equal(0, DurableStreamStore.Running(state.CapturePayload(), "billing"));
    }
}
