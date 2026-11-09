using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryPack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using SlimData;
using SlimData.Commands;
using SlimFaas.Database;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.Streams;

namespace SlimFaas.CollectionProbe;

public static class Program
{
    private static long _sink;
    private static readonly long Now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly CountType[] All = [CountType.Available, CountType.Running, CountType.WaitingForRetry];
    private static readonly CountType[] Available = [CountType.Available];

    public static void Main(string[] args)
    {
        var results = new List<Measurement>();
        foreach (int count in new[] { 0, 200, 2000 })
        foreach (int runningPercent in new[] { 0, 25, 100 })
        {
            var queue = Enumerable.Range(0, count).Select(index => new QueueElement(new byte[] { 1 }, "item", Now, 30, [],
                index % 100 < runningPercent ? [new QueueHttpTryElement(Now, reservedIp: "replica")] : [], [])).ToImmutableArray();
            Measure(results, $"count-all/{count}/{runningPercent}", () => Count(queue, All, int.MaxValue));
            Measure(results, $"count-available-10/{count}/{runningPercent}", () => Count(queue, Available, 10));
            Measure(results, $"dispatch/{count}/{runningPercent}", () => SlimDataService.BuildQueueDispatchState(queue, Now).TotalPending);
        }
        foreach (int count in new[] { 0, 200, 2000 })
        {
            var (payload, definition, subscription) = Streams(count);
            var service = new StreamService(null!, null!, null!, null!, new TrustedAccess(), null!,
                NullLogger<StreamService>.Instance, Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()));
            var context = new DefaultHttpContext();
            Measure(results, $"counters/{count}", () => DurableStreamStore.Counters(payload, definition, subscription).PendingCount);
            Measure(results, $"replica-ranking/{count}", () => Rank(payload));
            foreach (int limit in new[] { 1, 25 })
            {
                Measure(results, $"catalog-page-{limit}/{count}", () => Catalog(service, context, payload, limit));
                Measure(results, $"function-page-{limit}/{count}", () => Functions(service, context, payload, definition, limit));
            }
        }
        File.WriteAllText(args[0], JsonSerializer.Serialize(results, ProbeJsonContext.Default.ListMeasurement));
        Console.WriteLine(FormattableString.Invariant($"Wrote {results.Count} samples; sink={Volatile.Read(ref _sink)}"));
    }

    private static long Count(ImmutableArray<QueueElement> queue, CountType[] types, int maximum)
    {
#if PR504_BASELINE
        if (queue.IsDefaultOrEmpty) return 0;
        long count = 0;
        if (types.Contains(CountType.Available)) count += queue.GetQueueAvailableElement(Now, maximum).Length;
        if (types.Contains(CountType.Running)) count += queue.GetQueueRunningElement(Now).Length;
        if (types.Contains(CountType.WaitingForRetry)) count += queue.GetQueueWaitingForRetryElement(Now).Length;
        return count;
#else
        return SlimDataService.CountQueueElements(queue, types, maximum, Now);
#endif
    }

    private static long Rank(SlimDataPayload payload)
    {
        int sum = 0;
#if PR504_BASELINE
        foreach (string replica in new[] { "r0", "r1", "r2", "r3" }) sum += DurableStreamStore.Running(payload, "billing", replica);
#else
        var counts = DurableStreamStore.RunningByReplica(payload, "billing");
        foreach (string replica in new[] { "r0", "r1", "r2", "r3" }) sum += counts.GetValueOrDefault(replica);
#endif
        return sum;
    }

    private static long Catalog(StreamService service, HttpContext context, SlimDataPayload payload, int limit)
    {
#if PR504_BASELINE
        return service.Summaries(context, payload).Where(item => string.CompareOrdinal(item.Name, "s00000001") > 0).Take(limit + 1).Sum(item => item.Revision);
#else
        return service.Summaries(context, payload, "", "s00000001", limit + 1).Sum(item => item.Revision);
#endif
    }

    private static long Functions(StreamService service, HttpContext context, SlimDataPayload payload, DurableStreamDefinition definition, int limit)
    {
#if PR504_BASELINE
        return service.Functions(context, payload, definition).Where(item => string.CompareOrdinal(item.Function, "f00000001") > 0).Take(limit + 1).Sum(item => item.Revision);
#else
        return service.Functions(context, payload, definition, "f00000001", limit + 1).Sum(item => item.Revision);
#endif
    }

    private static (SlimDataPayload, DurableStreamDefinition, DurableStreamSubscription) Streams(int count)
    {
        var definitions = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var messages = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var pending = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var subscriptions = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var definition = new DurableStreamDefinition { Name = "orders", Generation = "g", Tail = count, Visibility = "Public" };
        var subscription = new DurableStreamSubscription { Function = "billing", Generation = "sub" };
        for (int index = 0; index < count; index++)
        {
            string name = "s" + index.ToString("D8", CultureInfo.InvariantCulture);
            definitions.Add(DurableStreamStore.DefinitionPrefix + name, MemoryPackSerializer.Serialize(
                new DurableStreamDefinition { Name = name, Visibility = "Public", Revision = index }));
            string id = "message-" + index.ToString(CultureInfo.InvariantCulture);
            messages.Add(id, MemoryPackSerializer.Serialize(new DurableStreamMessage { Id = id, Sequence = index + 1, ContentType = "application/json" }));
            pending.Add(id, MemoryPackSerializer.Serialize(new DurableStreamDelivery
            {
                MessageId = id, Function = "billing", SubscriptionGeneration = "sub", Replica = "r" + index % 4,
                State = index % 2 == 0 ? "Running" : "WaitingForCallback"
            }));
            name = "f" + index.ToString("D8", CultureInfo.InvariantCulture);
            subscriptions.Add(name, MemoryPackSerializer.Serialize(new DurableStreamSubscription { Function = name, Generation = "sub" }));
        }
        return (new SlimDataPayload
        {
            KeyValues = definitions.ToImmutable(), Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add("\0stream:messages:orders", messages.ToImmutable()).Add("\0stream:pending:orders:billing", pending.ToImmutable())
                .Add("\0stream:active-attempts", pending.ToImmutable()).Add("\0stream:subscriptions:orders", subscriptions.ToImmutable())
        }, definition, subscription);
    }

    private static void Measure(List<Measurement> results, string name, Func<long> operation)
    {
        long sink = 0;
        var timer = Stopwatch.StartNew();
        int iterations = 0;
        do { sink += operation(); iterations++; } while (timer.ElapsedMilliseconds < 100);
        for (int sample = 0; sample < 3; sample++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
            long started = Stopwatch.GetTimestamp();
            for (int index = 0; index < iterations; index++) sink += operation();
            double elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            results.Add(new(name, sample, iterations, elapsed / iterations, (double)allocated / iterations,
                GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2));
        }
        Volatile.Write(ref _sink, sink);
    }

    private sealed class TrustedAccess : IFunctionAccessPolicy
    {
        public bool IsInternalRequest(HttpContext context) => true;
        public bool CanAccessFunction(HttpContext context, DeploymentInformation function, string path) => true;
        public List<DeploymentInformation> GetAllowedSubscribers(HttpContext context, string eventName) => [];
    }
}

public sealed record Measurement(string Case, int Sample, int Iterations, double Nanoseconds, double Bytes,
    int Gen0, int Gen1, int Gen2);

[JsonSerializable(typeof(List<Measurement>))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
