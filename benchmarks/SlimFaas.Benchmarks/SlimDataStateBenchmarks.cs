using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using SlimData;
using SlimFaas.Benchmarks.Support;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 5 — cost of one <c>SlimDataStateSnapshot.PayloadBytes</c> evaluation
/// (full state walk: every key/value, every hashset field, every queue element).
/// <c>SlimPersistentState.PersistAsync</c> evaluated it 3 times per snapshot; this
/// benchmark quantifies what each avoided evaluation costs.
/// (Accessed through InternalsVisibleTo — the type is internal to SlimData.)
/// </summary>
[MemoryDiagnoser]
public class SlimDataStateBenchmarks
{
    private SlimDataStateSnapshot _snapshot = null!;

    [GlobalSetup]
    public void Setup()
    {
        var value = new byte[1024];
        Random.Shared.NextBytes(value);

        var keyValues = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        for (var i = 0; i < 200; i++)
            keyValues[$"key-{i:D4}"] = value;

        var hashsets = ImmutableDictionary
            .CreateBuilder<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>();
        for (var h = 0; h < 20; h++)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
            for (var f = 0; f < 20; f++)
                fields[$"field-{f:D3}"] = value;
            hashsets[$"hashset-{h:D3}"] = fields.ToImmutable();
        }

        var queues = ImmutableDictionary.CreateBuilder<string, ImmutableArray<QueueElement>>();
        for (var q = 0; q < 10; q++)
            queues[$"queue-{q:D3}"] = BenchData.BuildQueue(depth: 50, payloadBytes: 2048);

        _snapshot = new SlimDataStateSnapshot(
            hashsets.ToImmutable(),
            keyValues.ToImmutable(),
            queues.ToImmutable());
    }

    [Benchmark]
    public long PayloadBytes() => _snapshot.PayloadBytes;
}
