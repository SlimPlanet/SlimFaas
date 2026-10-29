using System.Collections.Immutable;
using MemoryPack;
using DotNext.IO;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class DeadLetterTests
{
    private static readonly long Now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static SlimDataState Create() => new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

    private static void Enable(SlimDataState state, int maxLength = 10000, int maxAge = 604800, bool enabled = true) =>
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Policy, Function = "billing", NowTicks = Now,
            Policy = new() { Enabled = enabled, MaxLength = maxLength, MaxAgeSeconds = maxAge }
        }, state);

    private static async Task Start(SlimDataState state, string id = "first", int[]? retries = null)
    {
        await SlimDataInterpreter.DoListLeftPushBatchAsync(new()
        {
            Items = [new() { Key = "Queue:billing", Identifier = id, NowTicks = Now, Value = new byte[] { 1, 2, 3 },
                RetryTimeout = 10, Retries = retries?.ToList() ?? [], HttpStatusCodesWorthRetrying = [500, 503, 504] }]
        }, state);
        await SlimDataInterpreter.DoListRightPopAsync(new() { Key = "Queue:billing", Count = 1, NowTicks = Now, IdTransaction = id }, state);
    }

    private static DeadLetterMutationResult Complete(SlimDataState state, int status, string id = "first", string source = "Http") =>
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Complete, Function = "billing", ElementId = id,
            HttpStatus = status, Source = source, NowTicks = Now + TimeSpan.TicksPerSecond
        }, state);

    private static DeadLetterEntry[] Entries(SlimDataState state) =>
        state.Hashsets.GetValueOrDefault(DeadLetterKeys.Messages("billing"))?.Values
            .Select(value => MemoryPackSerializer.Deserialize<DeadLetterEntry>(value.Span)!).ToArray() ?? [];

    [Theory]
    [InlineData(200, 0)]
    [InlineData(204, 0)]
    [InlineData(400, 1)]
    [InlineData(503, 1)]
    public async Task Final_outcomes_distinguish_success_and_nonretryable_failure(int status, int count)
    {
        var state = Create(); Enable(state); await Start(state);
        Complete(state, status);
        Assert.Equal(count, Entries(state).Length);
        Assert.Empty(state.Queues["Queue:billing"]);
        if (count != 0)
        {
            var entry = Assert.Single(Entries(state));
            Assert.NotEqual("first", entry.Id);
            Assert.Empty(entry.Value);
            Assert.Equal(new byte[] { 1, 2, 3 }, state.Hashsets[DeadLetterKeys.Payloads("billing")][entry.Id].ToArray());
            Assert.Equal(1, entry.Attempts);
            Assert.Equal(status, entry.LastHttpStatus);
            Assert.Equal(Now, entry.AcceptedAtTicks);
        }
    }

    [Fact]
    public async Task Disabled_capture_keeps_legacy_completion_and_does_not_remove_existing_dead_letters()
    {
        var state = Create(); Enable(state); await Start(state); Complete(state, 400);
        Enable(state, enabled: false); await Start(state, "second"); Complete(state, 500, "second");
        Assert.Single(Entries(state)); Assert.Empty(state.Queues["Queue:billing"]);
    }

    [Fact]
    public async Task Retryable_response_is_not_captured_until_retries_are_exhausted()
    {
        var state = Create(); Enable(state); await Start(state, retries: [0]);
        Complete(state, 503); Assert.Empty(Entries(state));
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 1, NowTicks = Now + 2 * TimeSpan.TicksPerSecond, IdTransaction = "retry"
        }, state);
        DeadLetterStateMachine.Apply(new()
        {
            Action = DeadLetterAction.Complete, Function = "billing", ElementId = "first",
            HttpStatus = 503, NowTicks = Now + 3 * TimeSpan.TicksPerSecond
        }, state);
        var entry = Assert.Single(Entries(state));
        Assert.Equal("RetriesExhausted", entry.FailureReason); Assert.Equal(2, entry.Attempts);
    }

    [Theory]
    [InlineData(false, "DeliveryTimeout")]
    [InlineData(true, "CallbackTimeout")]
    public async Task Sweep_captures_timeout_without_dispatching_again(bool callback, string reason)
    {
        var state = Create(); Enable(state); await Start(state);
        if (callback) Complete(state, 202);
        Assert.Single(state.Queues["Queue:billing"]);
        DeadLetterStateMachine.Apply(new() { Action = DeadLetterAction.Sweep, Function = "billing", NowTicks = Now + 11 * TimeSpan.TicksPerSecond }, state);
        var entry = Assert.Single(Entries(state));
        Assert.Equal(reason, entry.FailureReason); Assert.Equal(1, entry.Attempts);
        Assert.Empty(state.Queues["Queue:billing"]);
    }

    [Theory]
    [InlineData("Callback", "CallbackError")]
    [InlineData("Transport", "TransportError")]
    public async Task Outcome_origin_is_durable(string source, string reason)
    {
        var state = Create(); Enable(state); await Start(state); Complete(state, 500, source: source);
        Assert.Equal(reason, Assert.Single(Entries(state)).FailureReason);
    }

    [Fact]
    public async Task Repeated_completion_and_replay_are_idempotent_and_preserve_lineage()
    {
        var state = Create(); Enable(state); await Start(state); Complete(state, 400); Complete(state, 400);
        var entry = Assert.Single(Entries(state));
        DeadLetterMutation Replay(string newId) => new()
        {
            Action = DeadLetterAction.Requeue, Function = "billing", MessageId = entry.Id,
            NewElementId = newId, NowTicks = Now + 2 * TimeSpan.TicksPerSecond,
            RetryTimeoutSeconds = 20, Retries = [1, 2], HttpStatusRetries = [503]
        };
        var first = DeadLetterStateMachine.Apply(Replay("new"), state);
        var repeated = DeadLetterStateMachine.Apply(Replay("duplicate"), state);
        Assert.Equal(202, first.Status); Assert.Equal("new", repeated.Receipt!.ElementId);
        var active = Assert.Single(state.Queues["Queue:billing"]);
        Assert.Equal("new", active.Id); Assert.Empty(active.RetryQueueElements);
        Assert.Equal([1, 2], active.TimeoutRetriesSeconds.ToArray()); Assert.Empty(Entries(state));
        Complete(state, 200);
        Assert.Single(state.Queues["Queue:billing"]);
        Assert.Contains("first", DeadLetterKeys.GetPayloadOwnerIds(state.CapturePayload()));
        var expired = Replay("late"); expired.NowTicks += TimeSpan.TicksPerDay;
        Assert.Equal(404, DeadLetterStateMachine.Apply(expired, state).Status);
    }

    [Fact]
    public async Task Retention_evicts_oldest_and_expires_without_changing_acceptance_time()
    {
        var state = Create(); Enable(state, maxLength: 1, maxAge: 2);
        await Start(state); Complete(state, 400);
        await Start(state, "second"); Complete(state, 400, "second");
        Assert.Equal("second", Assert.Single(Entries(state)).OriginalElementId);
        DeadLetterStateMachine.Apply(new() { Action = DeadLetterAction.Sweep, Function = "billing", NowTicks = Now + 3 * TimeSpan.TicksPerSecond }, state);
        Assert.Empty(Entries(state));
        var stats = MemoryPackSerializer.Deserialize<DeadLetterStatistics>(state.KeyValues[DeadLetterKeys.Stats("billing")].Span)!;
        Assert.Equal(2, stats.Captured); Assert.Equal(1, stats.Evicted); Assert.Equal(1, stats.Expired);
    }

    [Fact]
    public async Task Timeout_excluded_from_retry_codes_is_captured_before_dequeue_can_drop_it()
    {
        var state = Create(); Enable(state); await Start(state, retries: [0, 0]);
        var original = state.Queues["Queue:billing"][0];
        state.Queues = state.Queues.SetItem("Queue:billing",
            [new(original.Value, original.Id, original.InsertTimeStamp, original.HttpTimeoutSeconds,
                original.TimeoutRetriesSeconds, original.RetryQueueElements, ImmutableHashSet.Create(503))]);
        var before = state.Capture();
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 1, NowTicks = Now + 11 * TimeSpan.TicksPerSecond, IdTransaction = "later"
        }, state);
        Assert.Empty(state.Queues["Queue:billing"]);
        Assert.Equal("DeliveryTimeout", Assert.Single(Entries(state)).FailureReason);
        Assert.Single(before.Queues["Queue:billing"]);
        Assert.Equal(0, before.Queues["Queue:billing"][0].RetryQueueElements[^1].EndTimeStamp);
    }

    [Fact]
    public async Task Timed_out_attempt_retries_before_terminal_capture_when_504_is_retryable()
    {
        var state = Create(); Enable(state); await Start(state, retries: [0]);
        await SlimDataInterpreter.DoListRightPopAsync(new()
        {
            Key = "Queue:billing", Count = 1, NowTicks = Now + 11 * TimeSpan.TicksPerSecond, IdTransaction = "retry"
        }, state);
        Assert.Empty(Entries(state));
        Assert.Equal(2, Assert.Single(state.Queues["Queue:billing"]).NumberOfTries());
        DeadLetterStateMachine.Apply(new() { Action = DeadLetterAction.Sweep, Function = "billing", NowTicks = Now + 22 * TimeSpan.TicksPerSecond }, state);
        Assert.Equal(2, Assert.Single(Entries(state)).Attempts);
    }

    [Fact]
    public async Task Snapshot_restores_retained_payload_and_receipts_after_restart()
    {
        var state = Create(); Enable(state); await Start(state); Complete(state, 400);
        string id = Assert.Single(Entries(state)).Id;
        using var stream = new MemoryStream();
        await SlimDataSnapshotSerializer.WriteAsync(IAsyncBinaryWriter.Create(stream, new byte[256]), state.Capture(), CancellationToken.None);
        stream.Position = 0;
        var restored = await SlimDataSnapshotSerializer.ReadAsync(IAsyncBinaryReader.Create(stream, new byte[256]), CancellationToken.None);
        var restarted = new SlimDataState(restored.Hashsets, restored.KeyValues, restored.Queues);
        Assert.Equal(id, Assert.Single(Entries(restarted)).Id);
        var mutation = new DeadLetterMutation { Action = DeadLetterAction.Requeue, Function = "billing", MessageId = id,
            NewElementId = "restarted", NowTicks = Now + 2 * TimeSpan.TicksPerSecond, RetryTimeoutSeconds = 10 };
        Assert.Equal(202, DeadLetterStateMachine.Apply(mutation, restarted).Status);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(restarted.Queues["Queue:billing"]).Value.ToArray());
        Assert.Empty(restarted.Hashsets[DeadLetterKeys.Payloads("billing")]);
        Assert.Equal("restarted", DeadLetterStateMachine.Apply(mutation, restarted).Receipt!.ElementId);
    }

    [Fact]
    public void Previous_batch_result_format_remains_readable_for_stored_producer_receipts()
    {
        var current = MemoryPackSerializer.Serialize(new SlimDataBatchOperationResult
        {
            Kind = SlimDataBatchOperationKind.ListLeftPush, RequestId = "old", Applied = true, ElementId = "original"
        });
        Assert.Equal(8, current[0]);
        Assert.Equal(255, current[^1]);
        var previous = current[..^1];
        previous[0] = 7;
        var restored = MemoryPackSerializer.Deserialize<SlimDataBatchOperationResult>(previous)!;
        Assert.Equal("original", restored.ElementId); Assert.Null(restored.DeadLetterResult);
    }
}
