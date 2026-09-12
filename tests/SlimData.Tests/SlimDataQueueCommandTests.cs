using System.Collections.Immutable;
using SlimData.Commands;

namespace SlimData.Tests;

/// <summary>
/// Non-regression tests pinning the observable behavior of the queue commands applied by
/// <see cref="SlimDataInterpreter"/> (dequeue, HTTP callbacks, push). They were written
/// against the historical multi-pass implementation and must stay green after the
/// single-pass rework.
/// </summary>
public sealed class SlimDataQueueCommandTests
{
    private const string Key = "queue";
    private static readonly long Now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long Second = TimeSpan.TicksPerSecond;

    // ---------------------------------------------------------------- ListRightPop

    [Fact]
    public async Task RightPop_reserves_available_elements_in_queue_order_with_reserved_ips_by_position()
    {
        var state = CreateState(
            Available("a"),
            Running("b", startedSecondsAgo: 1),
            Available("c"),
            Available("d"));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 2, "tx-1", "10.0.0.1", "10.0.0.2"), state);

        var queue = state.Queues[Key];
        Assert.Equal(["a", "b", "c", "d"], queue.Select(e => e.Id));
        Assert.Equal("tx-1", queue[0].RetryQueueElements[^1].IdTransaction);
        Assert.Equal("10.0.0.1", queue[0].RetryQueueElements[^1].ReservedIp);
        Assert.Equal(Now, queue[0].RetryQueueElements[^1].StartTimeStamp);
        Assert.Equal("tx-1", queue[2].RetryQueueElements[^1].IdTransaction);
        Assert.Equal("10.0.0.2", queue[2].RetryQueueElements[^1].ReservedIp);
        Assert.Empty(queue[3].RetryQueueElements);
        Assert.NotEqual("tx-1", queue[1].RetryQueueElements[^1].IdTransaction);
    }

    [Fact]
    public async Task RightPop_without_enough_reserved_ips_leaves_the_reserved_ip_empty()
    {
        var state = CreateState(Available("a"), Available("b"));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 2, "tx-1", "10.0.0.1"), state);

        var queue = state.Queues[Key];
        Assert.Equal("10.0.0.1", queue[0].RetryQueueElements[^1].ReservedIp);
        Assert.Equal(string.Empty, queue[1].RetryQueueElements[^1].ReservedIp);
    }

    [Fact]
    public async Task RightPop_marks_timed_out_tries_as_504_at_now()
    {
        // 30 s timeout, retries remain and 504 is retryable: the element stays and waits for its retry.
        var state = CreateState(Running("a", startedSecondsAgo: 60, retries: [2, 4], retryCodes: [504]));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 1, "tx-1"), state);

        var element = Assert.Single(state.Queues[Key]);
        var last = element.RetryQueueElements[^1];
        Assert.Equal(504, last.HttpCode);
        Assert.Equal(Now, last.EndTimeStamp);
        Assert.Single(element.RetryQueueElements); // still inside the retry window: not re-dispatched
    }

    [Fact]
    public async Task RightPop_evicts_timed_out_elements_without_retries()
    {
        var state = CreateState(
            Running("a", startedSecondsAgo: 60, retries: []),
            Available("b"));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 5, "tx-1"), state);

        var element = Assert.Single(state.Queues[Key]);
        Assert.Equal("b", element.Id);
        Assert.Equal("tx-1", element.RetryQueueElements[^1].IdTransaction);
    }

    [Fact]
    public async Task RightPop_evicts_finished_elements_and_keeps_the_order_of_the_rest()
    {
        var state = CreateState(
            Finished("a", 200),
            Available("b"),
            Finished("c", 404),
            Running("d", startedSecondsAgo: 1),
            Finished("e", 200));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 0, "tx-1"), state);

        Assert.Equal(["b", "d"], state.Queues[Key].Select(e => e.Id));
    }

    [Fact]
    public async Task RightPop_skips_running_and_waiting_for_retry_elements()
    {
        var state = CreateState(
            Running("running", startedSecondsAgo: 1),
            Finished("waiting", 500, endedSecondsAgo: 1, retries: [10], retryCodes: [500]),
            Finished("retry-window-elapsed", 500, endedSecondsAgo: 30, retries: [10], retryCodes: [500]),
            Available("fresh"));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 10, "tx-1"), state);

        var queue = state.Queues[Key];
        Assert.Equal(["running", "waiting", "retry-window-elapsed", "fresh"], queue.Select(e => e.Id));
        Assert.NotEqual("tx-1", queue[0].RetryQueueElements[^1].IdTransaction);
        Assert.Single(queue[1].RetryQueueElements);
        Assert.Equal(2, queue[2].RetryQueueElements.Length);
        Assert.Equal("tx-1", queue[2].RetryQueueElements[^1].IdTransaction);
        Assert.Equal("tx-1", queue[3].RetryQueueElements[^1].IdTransaction);
    }

    [Fact]
    public async Task RightPop_with_an_already_applied_transaction_id_does_not_reserve_again()
    {
        var state = CreateState(
            Running("a", startedSecondsAgo: 1, transactionId: "tx-1"),
            Available("b"),
            Finished("c", 200));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 5, "tx-1"), state);

        var queue = state.Queues[Key];
        Assert.Equal(["a", "b"], queue.Select(e => e.Id)); // finished element still evicted
        Assert.Empty(queue[1].RetryQueueElements);          // but nothing newly reserved
    }

    [Fact]
    public async Task RightPop_ignores_the_transaction_id_of_an_evicted_element()
    {
        var state = CreateState(
            Finished("a", 200, transactionId: "tx-1"),
            Available("b"));

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 1, "tx-1"), state);

        var element = Assert.Single(state.Queues[Key]);
        Assert.Equal("b", element.Id);
        Assert.Equal("tx-1", element.RetryQueueElements[^1].IdTransaction);
    }

    [Fact]
    public async Task RightPop_on_an_unknown_key_leaves_queues_untouched()
    {
        var state = CreateState(Available("a"));
        var before = state.Queues;

        var pop = Pop(count: 1, "tx-1");
        pop.Key = "other";
        await SlimDataInterpreter.DoListRightPopAsync(pop, state);

        Assert.Same(before, state.Queues);
        Assert.Empty(state.Queues[Key][0].RetryQueueElements);
    }

    [Fact]
    public async Task RightPop_on_an_empty_queue_keeps_the_empty_queue()
    {
        var state = CreateState();

        await SlimDataInterpreter.DoListRightPopAsync(Pop(count: 1, "tx-1"), state);

        Assert.True(state.Queues.TryGetValue(Key, out var queue));
        Assert.Empty(queue);
    }

    // ---------------------------------------------------------------- ListCallback

    [Fact]
    public async Task Callback_records_the_http_code_on_the_last_try_and_evicts_finished_elements()
    {
        var state = CreateState(
            Running("a", startedSecondsAgo: 1),
            Running("b", startedSecondsAgo: 1, retries: [10], retryCodes: [500]),
            Running("c", startedSecondsAgo: 1),
            Available("d"));

        await SlimDataInterpreter.DoListCallbackAsync(
            Callback(("a", 200), ("b", 500), ("unknown", 200)),
            state);

        var queue = state.Queues[Key];
        Assert.Equal(["b", "c", "d"], queue.Select(e => e.Id));
        var b = queue[0].RetryQueueElements[^1];
        Assert.Equal(500, b.HttpCode);
        Assert.Equal(Now, b.EndTimeStamp);
        Assert.Equal(0, queue[1].RetryQueueElements[^1].EndTimeStamp);
    }

    [Fact]
    public async Task Callback_delete_code_removes_the_element_even_without_tries()
    {
        var state = CreateState(Available("a"), Running("b", startedSecondsAgo: 1), Available("c"));

        await SlimDataInterpreter.DoListCallbackAsync(
            Callback(("a", SlimDataInterpreter.DeleteFromQueueCode), ("b", SlimDataInterpreter.DeleteFromQueueCode)),
            state);

        var element = Assert.Single(state.Queues[Key]);
        Assert.Equal("c", element.Id);
    }

    [Fact]
    public async Task Callback_with_a_status_on_an_element_without_tries_is_ignored()
    {
        var state = CreateState(Available("a"));

        await SlimDataInterpreter.DoListCallbackAsync(Callback(("a", 200)), state);

        var element = Assert.Single(state.Queues[Key]);
        Assert.Empty(element.RetryQueueElements);
    }

    [Fact]
    public async Task Callback_applies_duplicate_identifiers_in_order_and_stops_after_eviction()
    {
        var state = CreateState(
            Running("a", startedSecondsAgo: 1, retries: [10], retryCodes: [500]),
            Running("b", startedSecondsAgo: 1));

        await SlimDataInterpreter.DoListCallbackAsync(
            Callback(("a", 500), ("a", 200), ("b", SlimDataInterpreter.DeleteFromQueueCode), ("b", 500)),
            state);

        Assert.Empty(state.Queues[Key]);
    }

    [Fact]
    public async Task Callback_on_an_unknown_key_leaves_queues_untouched()
    {
        var state = CreateState(Running("a", startedSecondsAgo: 1));
        var before = state.Queues;

        var callback = Callback(("a", 200));
        callback.Key = "other";
        await SlimDataInterpreter.DoListCallbackAsync(callback, state);

        Assert.Same(before, state.Queues);
        Assert.Equal(0, state.Queues[Key][0].RetryQueueElements[^1].EndTimeStamp);
    }

    // ---------------------------------------------------------------- ListCallbackBatch

    [Fact]
    public async Task CallbackBatch_behaves_like_the_single_command_on_every_key()
    {
        var state = new SlimDataState(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
                .Add("q1", [Running("a", startedSecondsAgo: 1), Running("b", startedSecondsAgo: 1, retries: [10], retryCodes: [500]), Available("c")])
                .Add("q2", [Available("x"), Running("y", startedSecondsAgo: 1)])
                .Add("q3", [Running("z", startedSecondsAgo: 1)]));

        await SlimDataInterpreter.DoListCallbackBatchAsync(new ListCallbackBatchCommand
        {
            Items =
            [
                new() { Key = "q1", NowTicks = Now, CallbackElements = [new("a", 200), new("b", 500), new("c", 200), new("missing", 200)] },
                new() { Key = "q2", NowTicks = Now + Second, CallbackElements = [new("x", SlimDataInterpreter.DeleteFromQueueCode), new("y", SlimDataInterpreter.DeleteFromQueueCode), new("y", 200)] },
                new() { Key = "unknown", NowTicks = Now, CallbackElements = [new("z", 200)] },
                new() { Key = "q3", NowTicks = Now, CallbackElements = [] },
            ]
        }, state);

        var q1 = state.Queues["q1"];
        Assert.Equal(["b", "c"], q1.Select(e => e.Id));
        Assert.Equal(500, q1[0].RetryQueueElements[^1].HttpCode);
        Assert.Equal(Now, q1[0].RetryQueueElements[^1].EndTimeStamp);
        Assert.Empty(q1[1].RetryQueueElements);
        Assert.Empty(state.Queues["q2"]);
        Assert.Equal(0, Assert.Single(state.Queues["q3"]).RetryQueueElements[^1].EndTimeStamp);
        Assert.False(state.Queues.ContainsKey("unknown"));
    }

    [Fact]
    public async Task CallbackBatch_uses_the_now_ticks_of_each_item()
    {
        var state = CreateState(
            Running("a", startedSecondsAgo: 1, retries: [10], retryCodes: [500]),
            Running("b", startedSecondsAgo: 1, retries: [10], retryCodes: [500]));

        await SlimDataInterpreter.DoListCallbackBatchAsync(new ListCallbackBatchCommand
        {
            Items =
            [
                new() { Key = Key, NowTicks = Now, CallbackElements = [new("a", 500)] },
                new() { Key = Key, NowTicks = Now + Second, CallbackElements = [new("b", 500)] },
            ]
        }, state);

        var queue = state.Queues[Key];
        Assert.Equal(Now, queue[0].RetryQueueElements[^1].EndTimeStamp);
        Assert.Equal(Now + Second, queue[1].RetryQueueElements[^1].EndTimeStamp);
    }

    // ---------------------------------------------------------------- ListLeftPushBatch

    [Fact]
    public async Task LeftPushBatch_appends_new_identifiers_and_skips_duplicates_in_and_across_batches()
    {
        var state = CreateState(Running("a", startedSecondsAgo: 1));

        await SlimDataInterpreter.DoListLeftPushBatchAsync(new ListLeftPushBatchCommand
        {
            Items =
            [
                Push(Key, "a"),
                Push(Key, "b"),
                Push(Key, "b"),
                Push("other", "a"),
            ]
        }, state);

        Assert.Equal(["a", "b"], state.Queues[Key].Select(e => e.Id));
        Assert.Single(state.Queues[Key][0].RetryQueueElements); // the existing element is untouched
        Assert.Equal(["a"], state.Queues["other"].Select(e => e.Id));
        var pushed = state.Queues[Key][1];
        Assert.Equal(Now, pushed.InsertTimeStamp);
        Assert.Equal(30, pushed.HttpTimeoutSeconds);
        Assert.Equal([2, 4], pushed.TimeoutRetriesSeconds.ToArray());
        Assert.Equal([500], pushed.HttpStatusRetries.ToArray());
        Assert.Empty(pushed.RetryQueueElements);
    }

    // ---------------------------------------------------------------- helpers

    private static SlimDataState CreateState(params QueueElement[] elements) => new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty.Add(Key, [.. elements]));

    private static ListRightPopCommand Pop(int count, string transactionId, params string[] reservedIps) => new()
    {
        Key = Key,
        Count = count,
        NowTicks = Now,
        IdTransaction = transactionId,
        ReservedIps = [.. reservedIps]
    };

    private static ListCallbackCommand Callback(params (string Id, int HttpCode)[] callbacks) => new()
    {
        Key = Key,
        NowTicks = Now,
        CallbackElements = callbacks.Select(c => new CallbackElement(c.Id, c.HttpCode)).ToList()
    };

    private static ListLeftPushBatchCommand.BatchItem Push(string key, string id) => new()
    {
        Key = key,
        Identifier = id,
        NowTicks = Now,
        RetryTimeout = 30,
        Retries = [2, 4],
        HttpStatusCodesWorthRetrying = [500],
        Value = new byte[] { 1, 2, 3 }
    };

    private static QueueElement Available(string id) => Element(id, ImmutableArray<QueueHttpTryElement>.Empty, [2, 4], [500]);

    private static QueueElement Running(
        string id,
        int startedSecondsAgo,
        int[]? retries = null,
        int[]? retryCodes = null,
        string transactionId = "tx-old") => Element(
            id,
            [new QueueHttpTryElement(Now - startedSecondsAgo * Second, transactionId)],
            retries ?? [2, 4],
            retryCodes ?? [500]);

    private static QueueElement Finished(
        string id,
        int httpCode,
        int endedSecondsAgo = 1,
        int[]? retries = null,
        int[]? retryCodes = null,
        string transactionId = "tx-old") => Element(
            id,
            [new QueueHttpTryElement(Now - (endedSecondsAgo + 1) * Second, transactionId, Now - endedSecondsAgo * Second, httpCode)],
            retries ?? [2, 4],
            retryCodes ?? [500]);

    private static QueueElement Element(string id, ImmutableArray<QueueHttpTryElement> tries, int[] retries, int[] retryCodes) => new(
        new byte[] { 1 },
        id,
        Now - 10 * Second,
        httpTimeoutSeconds: 30,
        [.. retries],
        tries,
        [.. retryCodes]);
}
