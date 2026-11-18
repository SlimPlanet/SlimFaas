using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using SlimData;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class QueueSelectionBenchmarks
{
    private ImmutableArray<QueueElement> _queue;
    private long _now;

    [Params(0, 200, 2000)]
    public int Depth { get; set; }

    [Params(0, 25, 100)]
    public int RunningPercent { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _now = DateTime.UtcNow.Ticks;
        var builder = ImmutableArray.CreateBuilder<QueueElement>(Depth);
        for (int i = 0; i < Depth; i++)
        {
            bool running = i % 100 < RunningPercent;
            builder.Add(new(new byte[] { 1 }, "message-" + i, _now, 120, [],
                running ? [new(_now, "transaction")] : [], []));
        }
        _queue = builder.ToImmutable();
    }

    [Benchmark]
    public ImmutableArray<QueueElement> Running() => _queue.GetQueueRunningElement(_now);
}
