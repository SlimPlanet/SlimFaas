using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using MemoryPack;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class SlimDataCommandBatchTests
{
    [Fact]
    public async Task Composite_batch_applies_set_delete_set_in_request_order()
    {
        var state = NewState();
        var request = NewRequest(
            KeyValue("set-1", "key", "first"),
            Operation(SlimDataBatchOperationKind.DeleteKeyValue, "delete", "key"),
            KeyValue("set-2", "key", "second"));

        var response = await ApplyAsync(state, request);

        Assert.Equal("second", Encoding.UTF8.GetString(state.KeyValues["key"].Span));
        Assert.All(response.Results, static result => Assert.True(result.Applied));
    }

    [Fact]
    public async Task Composite_batch_applies_hashset_then_hash_delete_in_request_order()
    {
        var state = NewState();
        var add = Operation(SlimDataBatchOperationKind.AddHashSet, "hash-set", "hash");
        add.HashValues = new Dictionary<string, byte[]>
        {
            ["keep"] = [1],
            ["remove"] = [2]
        };
        var delete = Operation(SlimDataBatchOperationKind.DeleteHashSet, "hash-delete", "hash");
        delete.DictionaryKey = "remove";

        await ApplyAsync(state, NewRequest(add, delete));

        Assert.True(state.Hashsets["hash"].ContainsKey("keep"));
        Assert.False(state.Hashsets["hash"].ContainsKey("remove"));
    }

    [Fact]
    public async Task Composite_batch_applies_push_pop_callback_in_request_order()
    {
        var state = NewState();
        var push = Operation(SlimDataBatchOperationKind.ListLeftPush, "push", "queue");
        push.ElementId = "element";
        push.Value = [42];
        push.RetryTimeoutSeconds = 30;
        push.Retries = [1];
        push.HttpStatusRetries = [500];
        push.NowTicks = 100;

        var pop = Operation(SlimDataBatchOperationKind.ListRightPop, "pop", "queue");
        pop.TransactionId = "transaction";
        pop.Count = 1;
        pop.NowTicks = 101;

        var callback = Operation(SlimDataBatchOperationKind.ListCallback, "callback", "queue");
        callback.CallbackItems =
        [
            new QueueItemStatus("element", SlimDataInterpreter.DeleteFromQueueCode)
        ];
        callback.NowTicks = 102;

        var response = await ApplyAsync(state, NewRequest(push, pop, callback));

        Assert.Equal("element", Assert.Single(response.Results[1].QueueItems!).Id);
        Assert.Empty(state.Queues["queue"]);
    }

    [Fact]
    public async Task Composite_batch_coalesces_adjacent_pushes_without_changing_fifo_or_deduplication()
    {
        var state = NewState();
        SlimDataBatchOperation[] pushes = Enumerable.Range(0, 64)
            .Select(index =>
            {
                var operation = Operation(
                    SlimDataBatchOperationKind.ListLeftPush,
                    $"push-{index}",
                    "queue");
                operation.ElementId = $"element-{index}";
                operation.Value = [(byte)index];
                operation.RetryTimeoutSeconds = 30;
                return operation;
            })
            .ToArray();
        var duplicate = Operation(SlimDataBatchOperationKind.ListLeftPush, "duplicate", "queue");
        duplicate.ElementId = "element-0";
        duplicate.Value = [255];
        var callback = Operation(SlimDataBatchOperationKind.ListCallback, "callback", "queue");
        callback.CallbackItems = Enumerable.Range(0, 64)
            .Where(static index => index % 2 == 0)
            .Select(index => new QueueItemStatus(
                $"element-{index}",
                SlimDataInterpreter.DeleteFromQueueCode))
            .ToArray();

        SlimDataCommandBatchResponse response = await ApplyAsync(
            state,
            NewRequest([.. pushes, duplicate, callback]));

        Assert.Equal(66, response.Results.Length);
        Assert.Equal(
            Enumerable.Range(0, 64).Where(static index => index % 2 != 0).Select(index => $"element-{index}"),
            state.Queues["queue"].Select(static element => element.Id));
        Assert.DoesNotContain(state.Queues["queue"], static element => element.Value.Span[0] == 255);
    }

    [Fact]
    public async Task Pop_response_reuses_the_full_queue_payload_buffer_without_a_replica_side_copy()
    {
        var state = NewState();
        var push = Operation(SlimDataBatchOperationKind.ListLeftPush, "push", "queue");
        push.ElementId = "element";
        push.Value = new byte[256 * 1024];
        push.RetryTimeoutSeconds = 30;
        var pop = Operation(SlimDataBatchOperationKind.ListRightPop, "pop", "queue");
        pop.TransactionId = "transaction";
        pop.Count = 1;
        pop.NowTicks = 2;

        SlimDataCommandBatchResponse response = await ApplyAsync(state, NewRequest(push, pop));

        Assert.True(MemoryMarshal.TryGetArray(
            state.Queues["queue"][0].Value,
            out ArraySegment<byte> stored));
        Assert.Same(stored.Array, Assert.Single(response.Results[1].QueueItems!).Data);
    }

    [Fact]
    public async Task Retried_composite_batch_returns_cached_response_without_incrementing_twice()
    {
        var state = NewState();
        var increment = Operation(SlimDataBatchOperationKind.KeyValue, "increment", "counter");
        increment.KeyValueOperation = KeyValueOperation.IncrementInteger;
        increment.IntegerDelta = 1;
        increment.NowTicks = 100;
        var request = NewRequest(increment);

        var first = await ApplyAsync(state, request);
        var duplicate = await ApplyAsync(state, request);

        Assert.Equal(1, Assert.Single(first.Results).KeyValueResult!.IntegerValue);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(1, Assert.Single(duplicate.Results).KeyValueResult!.IntegerValue);
        Assert.Equal("1", Encoding.UTF8.GetString(state.KeyValues["counter"].Span));
    }

    [Fact]
    public async Task Sequence_gap_is_rejected_without_applying_mutations()
    {
        var state = NewState();
        var request = NewRequest(KeyValue("set", "key", "value"));
        request.Sequence = 2;

        var response = await ApplyAsync(state, request);

        Assert.True(response.SequenceGap);
        Assert.Equal(1, response.ExpectedSequence);
        Assert.False(state.KeyValues.ContainsKey("key"));
    }

    [Fact]
    public async Task Identical_composite_command_produces_identical_state_on_three_replicas()
    {
        var request = NewRequest(
            KeyValue("set", "key", "value"),
            Operation(SlimDataBatchOperationKind.DeleteKeyValue, "delete", "missing"));
        var states = new[] { NewState(), NewState(), NewState() };

        foreach (var state in states)
            await ApplyAsync(state, request);

        var expected = states[0].KeyValues
            .OrderBy(static item => item.Key)
            .Select(static item => (item.Key, Value: Convert.ToHexString(item.Value.Span)))
            .ToArray();
        foreach (var state in states.Skip(1))
        {
            Assert.Equal(
                expected,
                state.KeyValues
                    .OrderBy(static item => item.Key)
                    .Select(static item => (item.Key, Value: Convert.ToHexString(item.Value.Span)))
                    .ToArray());
        }
    }

    [Theory]
    [InlineData(SlimDataBatchOperationKind.ListLeftPush, true)]
    [InlineData(SlimDataBatchOperationKind.ListRightPop, true)]
    [InlineData(SlimDataBatchOperationKind.ListCallback, true)]
    [InlineData(SlimDataBatchOperationKind.KeyValue, false)]
    [InlineData(SlimDataBatchOperationKind.AddHashSet, false)]
    public void Queue_signal_classifier_only_selects_durable_queue_mutations(
        SlimDataBatchOperationKind kind,
        bool expected)
    {
        var envelope = new SlimDataRaftBatchEnvelope
        {
            Requests = [NewRequest(Operation(kind, "operation", "key"))]
        };

        Assert.Equal(expected, SlimDataCommandBatchCoordinator.ContainsQueueMutation(envelope));
    }

    private static async Task<SlimDataCommandBatchResponse> ApplyAsync(
        SlimDataState state,
        SlimDataCommandBatchRequest request)
    {
        var context = new SlimDataCommandBatchContext();
        var command = new ExecuteBatchCommand
        {
            Payload = SlimDataRaftBatchEnvelopeCodec.Serialize(new SlimDataRaftBatchEnvelope
            {
                Requests = [request]
            })
        };

        await SlimDataInterpreter.DoExecuteBatchAsync(command, state, context);
        return Assert.Single(context.Responses);
    }

    private static SlimDataCommandBatchRequest NewRequest(params SlimDataBatchOperation[] operations)
        => new()
        {
            ProducerId = "producer-1",
            GenerationId = "generation-1",
            Sequence = 1,
            RequestId = "batch-1",
            Operations = operations
        };

    private static SlimDataBatchOperation KeyValue(
        string requestId,
        string key,
        string value)
    {
        var operation = Operation(SlimDataBatchOperationKind.KeyValue, requestId, key);
        operation.KeyValueOperation = KeyValueOperation.Set;
        operation.Value = Encoding.UTF8.GetBytes(value);
        return operation;
    }

    private static SlimDataBatchOperation Operation(
        SlimDataBatchOperationKind kind,
        string requestId,
        string key)
        => new()
        {
            Kind = kind,
            RequestId = requestId,
            Key = key,
            NowTicks = 1
        };

    private static SlimDataState NewState()
        => new(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
}
