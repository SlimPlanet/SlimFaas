using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using SlimData;
using SlimData.Commands;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class DeadLetterMetadataBenchmarks
{
    [Params(0, 1048576)]
    public int PayloadBytes { get; set; }

    private SlimDataState _state = null!;
    private SlimDataPayload _payload;
    private DeadLetterMutation _sweep = null!;

    [GlobalSetup]
    public void Setup()
    {
        const string function = "benchmark";
        long now = DateTime.UtcNow.Ticks;
        var entries = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var bodies = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var body = new byte[PayloadBytes];
        for (int i = 0; i < 1000; i++)
        {
            string id = "dlq-" + i;
            entries.Add(id, MemoryPackSerializer.Serialize(new DeadLetterEntry
            {
                Id = id, OriginalElementId = "element-" + i, Function = function,
                FailedAtTicks = now, ExpiresAtTicks = now + TimeSpan.TicksPerDay
            }));
            bodies.Add(id, body);
        }
        _state = new(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add(DeadLetterKeys.Messages(function), entries.ToImmutable())
                .Add(DeadLetterKeys.Payloads(function), bodies.ToImmutable()),
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(DeadLetterKeys.Policy(function),
                MemoryPackSerializer.Serialize(new DeadLetterPolicy { Enabled = true })),
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
        _payload = _state.CapturePayload();
        _sweep = new() { Action = DeadLetterAction.Sweep, Function = function, NowTicks = now };
    }

    [Benchmark]
    public int PayloadOwnerReferences() => DeadLetterKeys.GetPayloadOwnerIds(_payload).Count();

    [Benchmark]
    public DeadLetterMutationResult RetentionSweep() => DeadLetterStateMachine.Apply(_sweep, _state);
}
