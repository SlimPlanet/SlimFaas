using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using MemoryPack;
using SlimData;
using SlimData.ClusterFiles;
using SlimData.Commands;
using SlimFaas.Benchmarks.Support;

namespace SlimFaas.Benchmarks;

[MemoryDiagnoser]
public class DataFeatureBenchmarks
{
    private ImmutableArray<QueueElement> _queue;
    private SlimDataPayload _payload;
    private DurableStreamDefinition _definition = null!;
    private DurableStreamSubscription _subscription = null!;
    private Dictionary<string, string> _tags = null!;
    private long _now;

    [Params(200, 2000)]
    public int Count { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _now = DateTime.UtcNow.Ticks;
        _queue = BenchData.BuildMixedStateQueue(Count, _now);
        _definition = new() { Name = "orders", Generation = "stream", Tail = Count };
        _subscription = new() { Function = "billing", Generation = "subscription" };
        var messages = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var deliveries = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var active = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        _tags = new(StringComparer.Ordinal);
        for (int i = 0; i < Count; i++)
        {
            string id = "message-" + i;
            messages.Add(id, MemoryPackSerializer.Serialize(new DurableStreamMessage { Id = id, Sequence = i + 1 }));
            string state = (i % 4) switch
            {
                0 => "Running", 1 => "WaitingForCallback", 2 => "RetryScheduled", _ => "Failed"
            };
            byte[] bytes = MemoryPackSerializer.Serialize(new DurableStreamDelivery
            {
                MessageId = id, Function = "billing", SubscriptionGeneration = "subscription",
                Replica = "replica", State = state
            });
            deliveries.Add(id, bytes);
            if (i % 4 < 2) active.Add(id, bytes);
            _tags.Add("tag-" + i, "value-" + i);
        }
        var hashes = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
            .Add("\0stream:messages:orders", messages.ToImmutable())
            .Add("\0stream:pending:orders:billing", deliveries.ToImmutable())
            .Add("\0stream:active-attempts", active.ToImmutable());
        _payload = new SlimDataState(hashes, ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty).CapturePayload();
    }

    [Benchmark]
    public ImmutableArray<QueueElement> RunningQueue() => _queue.GetQueueRunningElement(_now);

    [Benchmark]
    public ImmutableArray<QueueElement> RetryQueue() => _queue.GetQueueWaitingForRetryElement(_now);

    [Benchmark]
    public DurableStreamCounters StreamCounters() => DurableStreamStore.Counters(_payload, _definition, _subscription);

    [Benchmark]
    public int StreamRunning() => DurableStreamStore.Running(_payload, "billing", "replica");

    [Benchmark]
    public string? FileTags() => FileSyncProtocol.BuildTagsHeaderValue(_tags);
}
