using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using SlimData;
using SlimData.Commands;
using SlimFaas.Benchmarks.Support;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 6 — SlimData queue commands as applied by the Raft state machine
/// (<c>SlimDataInterpreter</c>), on the leader and on every follower, for every
/// dequeue and every HTTP callback:
/// - <see cref="ListRightPop"/>: one dequeue of <c>PopCount</c> elements from a queue
///   holding a realistic mix of available / running / timed-out / finished elements.
/// - <see cref="ListCallback"/> / <see cref="ListCallbackBatch"/>: <c>CallbackCount</c>
///   HTTP results reported against a queue of in-flight elements (half of them finish
///   the element, half ask for its deletion), through the single and the batched
///   command respectively.
/// - <see cref="ListLeftPushBatch"/>: 10 new messages appended to the queue.
/// The commands mutate the queue elements in place, so each invocation consumes its
/// own pristine state from a pool rebuilt in the (unmeasured) iteration setup.
/// (Accessed through InternalsVisibleTo — the interpreter methods are internal.)
/// </summary>
[MemoryDiagnoser]
[InvocationCount(Invocations, 1)]
public class SlimDataQueueBenchmarks
{
    private const int Invocations = 16;
    private const string Key = "queue";
    private const int PopCount = 10;
    private const int CallbackCount = 50;
    private const int PushCount = 10;

    [Params(50, 500)]
    public int QueueDepth { get; set; }

    private long _nowTicks;
    private readonly SlimDataState[] _states = new SlimDataState[Invocations];
    private int _next;
    private ListRightPopCommand _pop;
    private ListCallbackCommand _callback;
    private ListCallbackBatchCommand _callbackBatch;
    private ListLeftPushBatchCommand _push;

    [GlobalSetup]
    public void Setup()
    {
        _nowTicks = DateTime.UtcNow.Ticks;

        _pop = new ListRightPopCommand
        {
            Key = Key,
            Count = PopCount,
            NowTicks = _nowTicks,
            IdTransaction = "tx-pop",
            ReservedIps = Enumerable.Range(0, PopCount).Select(i => $"10.0.0.{i}").ToList()
        };

        // Callbacks target elements spread over the whole queue; even ones finish the
        // element (200), odd ones ask for its removal (DeleteFromQueueCode).
        var callbacks = new List<CallbackElement>(CallbackCount);
        for (var i = 0; i < CallbackCount; i++)
        {
            var target = (int)((long)i * QueueDepth / CallbackCount);
            callbacks.Add(new CallbackElement(
                $"element-{target:D6}",
                i % 2 == 0 ? 200 : SlimDataInterpreter.DeleteFromQueueCode));
        }

        _callback = new ListCallbackCommand { Key = Key, NowTicks = _nowTicks, CallbackElements = callbacks };
        _callbackBatch = new ListCallbackBatchCommand
        {
            Items =
            [
                new ListCallbackBatchCommand.BatchItem { Key = Key, NowTicks = _nowTicks, CallbackElements = callbacks }
            ]
        };

        var payload = new byte[256];
        Random.Shared.NextBytes(payload);
        _push = new ListLeftPushBatchCommand
        {
            Items = Enumerable.Range(0, PushCount).Select(i => new ListLeftPushBatchCommand.BatchItem
            {
                Key = Key,
                Identifier = $"new-{i:D6}",
                NowTicks = _nowTicks,
                RetryTimeout = 30,
                Retries = [2, 4, 8],
                HttpStatusCodesWorthRetrying = [500, 502, 503, 504],
                Value = payload
            }).ToList()
        };
    }

    [IterationSetup(Target = nameof(ListRightPop))]
    public void SetupMixedStates() => Fill(() => BenchData.BuildMixedStateQueue(QueueDepth, _nowTicks));

    [IterationSetup(Targets = [nameof(ListCallback), nameof(ListCallbackBatch), nameof(ListLeftPushBatch)])]
    public void SetupRunningStates() => Fill(() => BenchData.BuildRunningQueue(QueueDepth, _nowTicks));

    [Benchmark]
    public ValueTask ListRightPop() => SlimDataInterpreter.DoListRightPopAsync(_pop, _states[_next++]);

    [Benchmark]
    public ValueTask ListCallback() => SlimDataInterpreter.DoListCallbackAsync(_callback, _states[_next++]);

    [Benchmark]
    public ValueTask ListCallbackBatch() => SlimDataInterpreter.DoListCallbackBatchAsync(_callbackBatch, _states[_next++]);

    [Benchmark]
    public ValueTask ListLeftPushBatch() => SlimDataInterpreter.DoListLeftPushBatchAsync(_push, _states[_next++]);

    private void Fill(Func<ImmutableArray<QueueElement>> queue)
    {
        for (var i = 0; i < Invocations; i++)
            _states[i] = CreateState(queue());
        _next = 0;
    }

    private static SlimDataState CreateState(ImmutableArray<QueueElement> queue) => new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty.Add(Key, queue));
}
