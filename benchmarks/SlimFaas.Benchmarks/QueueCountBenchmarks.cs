using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using SlimData;
using SlimFaas.Benchmarks.Support;

namespace SlimFaas.Benchmarks;

/// <summary>
/// Theme 2 — counting the elements of a queue.
/// A/B comparison of both strategies in the same run:
/// - <see cref="MaterializeThenCount"/> reproduces the historical path of
///   SlimDataService.DoListCountElementAsync: one QueueData materialized per element,
///   with a full copy of the message body (Value.ToArray()), only for the caller to
///   read .Count in the end.
/// - <see cref="CountOnly"/> reproduces the "count-only" path: length of the filtered
///   arrays, without copying the payloads.
/// These paths are called by MetricsWorker (3×/function/s), SlimJobsWorker
/// (2×/config/s) and the SSE status cache.
/// </summary>
[MemoryDiagnoser]
public class QueueCountBenchmarks
{
    [Params(50, 500)]
    public int QueueDepth { get; set; }

    private ImmutableArray<QueueElement> _queue;
    private long _nowTicks;

    [GlobalSetup]
    public void Setup()
    {
        _queue = BenchData.BuildQueue(QueueDepth, payloadBytes: 2048);
        _nowTicks = DateTime.UtcNow.Ticks;
    }

    [Benchmark(Baseline = true)]
    public int MaterializeThenCount()
    {
        var result = new List<QueueElement>();
        result.AddRange(_queue.GetQueueAvailableElement(_nowTicks, int.MaxValue));
        return result
            .Select(static item => new QueueData(
                item.Id,
                item.Value.ToArray(),
                item.NumberOfTries(),
                item.IsLastTry(),
                item.GetLastRetryTimeTicks(),
                item.GetHttpTimeoutTicks(),
                item.GetLastReservedIp()))
            .ToList()
            .Count;
    }

    [Benchmark]
    public int CountOnly() => _queue.GetQueueAvailableElement(_nowTicks, int.MaxValue).Length;
}
