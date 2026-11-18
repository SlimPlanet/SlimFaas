using System.Collections.Immutable;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using SlimData;
using SlimFaas.Benchmarks.Support;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class QueuePipelineBenchmarks
{
    private ImmutableArray<QueueElement> _queue;
    private long _now;

    [Params(0, 200, 2000)]
    public int Count { get; set; }

    [Params(false, true)]
    public bool Retry { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _now = DateTime.UtcNow.Ticks;
        _queue = BenchData.BuildMixedStateQueue(Count, _now);
    }

    [Benchmark(Baseline = true)]
    public ImmutableArray<QueueElement> CurrentZLinq() => SelectZLinq(_queue, _now, Retry);

    [Benchmark]
    public ImmutableArray<QueueElement> DotNetLinq() => SelectLinq(_queue, _now, Retry);

    [Benchmark]
    public ImmutableArray<QueueElement> DirectLoop() => SelectLoop(_queue, _now, Retry);

    internal static ImmutableArray<QueueElement> SelectZLinq(ImmutableArray<QueueElement> queue, long now, bool retry)
        => retry ? queue.GetQueueWaitingForRetryElement(now) : queue.GetQueueRunningElement(now);

    internal static ImmutableArray<QueueElement> SelectLinq(ImmutableArray<QueueElement> queue, long now, bool retry)
    {
        if (queue.IsEmpty) return ImmutableArray<QueueElement>.Empty;
        return ImmutableCollectionsMarshal.AsImmutableArray(queue.Where(element => retry ? element.IsWaitingForRetry(now) : element.IsRunning(now)).ToArray());
    }

    internal static ImmutableArray<QueueElement> SelectLoop(ImmutableArray<QueueElement> queue, long now, bool retry)
    {
        if (queue.IsEmpty) return ImmutableArray<QueueElement>.Empty;
        var selected = new List<QueueElement>();
        foreach (QueueElement element in queue)
            if (retry ? element.IsWaitingForRetry(now) : element.IsRunning(now)) selected.Add(element);
        return ImmutableCollectionsMarshal.AsImmutableArray(selected.ToArray());
    }
}
