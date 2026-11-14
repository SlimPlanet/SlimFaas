using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MemoryPack;
using SlimData.ClusterFiles;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class CollectionSelectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    [InlineData(2048)]
    public void Dense_queue_selection_owns_its_result_array(int count)
    {
        long now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var builder = ImmutableArray.CreateBuilder<QueueElement>(count);
        for (int i = 0; i < count; i++)
            builder.Add(new(new byte[] { 1 }, "message-" + i, now, 120, [], [new(now, "transaction")], []));
        var source = builder.ToImmutable();

        var selected = source.GetQueueRunningElement(now);

        Assert.Equal(count, selected.Length);
        for (int i = 0; i < count; i++) Assert.Same(source[i], selected[i]);
        // Mutate through the test-only escape hatch to verify the returned array
        // belongs to the result, while queue elements themselves stay shared.
        var array = ImmutableCollectionsMarshal.AsArray(selected)!;
        array[0] = new(new byte[] { 2 }, "replacement", now, 120, [], [], []);
        Assert.Equal("message-0", source[0].Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2048)]
    public void Sparse_queue_selection_preserves_order_and_element_ownership(int available)
    {
        long now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var first = new QueueElement(new byte[] { 1 }, "first", now, 30, [], [new(now, "first")], []);
        var last = new QueueElement(new byte[] { 2 }, "last", now, 30, [], [new(now, "last")], []);
        var queue = ImmutableArray.CreateBuilder<QueueElement>();
        queue.Add(first);
        for (int i = 0; i < available; i++)
            queue.Add(new(new byte[] { 3 }, "available-" + i, now, 30, [], [], []));
        queue.Add(last);
        var original = queue.ToImmutable();

        var selected = original.GetQueueRunningElement(now);

        Assert.Equal(2, selected.Length);
        Assert.Same(first, selected[0]);
        Assert.Same(last, selected[1]);
        Assert.Equal(available + 2, original.Length);
        Assert.Empty(original.GetQueueWaitingForRetryElement(now));
        Assert.Empty(original.GetQueueFinishedElement(now));
        Assert.Empty(original.GetQueueTimeoutElement(now));
        Assert.Empty(ImmutableArray<QueueElement>.Empty.GetQueueRunningElement(now));
    }

    [Fact]
    public void Stream_counters_ignore_stale_generations_and_messages_outside_retention()
    {
        var messages = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var deliveries = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        string[] states = ["Running", "WaitingForCallback", "RetryScheduled", "Failed", "Failed", "Failed"];
        for (int i = 0; i < states.Length; i++)
        {
            string id = "message-" + i;
            if (i != 5)
                messages.Add(id, MemoryPackSerializer.Serialize(new DurableStreamMessage { Id = id }));
            deliveries.Add(id, MemoryPackSerializer.Serialize(new DurableStreamDelivery
            {
                MessageId = id, State = states[i], SubscriptionGeneration = i == 4 ? "old" : "current"
            }));
        }
        var state = new SlimDataState(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add("\0stream:messages:orders", messages.ToImmutable())
                .Add("\0stream:pending:orders:billing", deliveries.ToImmutable()),
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

        var counts = DurableStreamStore.Counters(state.CapturePayload(),
            new() { Name = "orders", Tail = 6 }, new() { Function = "billing", Generation = "current" });

        Assert.Equal(new DurableStreamCounters(6, 4, 1, 1, 1, 5), counts);
    }

    [Fact]
    public void File_tags_keep_ordinal_order_and_round_trip_delimiters_and_unicode()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["z"] = "a;b=c|d", ["A"] = "été", ["a"] = "value"
        };
        string expected = string.Join(';',
            Base64UrlCodec.Encode("A") + "=" + Base64UrlCodec.Encode("été"),
            Base64UrlCodec.Encode("a") + "=" + Base64UrlCodec.Encode("value"),
            Base64UrlCodec.Encode("z") + "=" + Base64UrlCodec.Encode("a;b=c|d"));

        string? encoded = FileSyncProtocol.BuildTagsHeaderValue(tags);

        Assert.Equal(expected, encoded);
        Assert.True(FileSyncProtocol.TryParseTagsHeaderValue(encoded, out var decoded));
        Assert.Equal(tags.Count, decoded!.Count);
        foreach (var item in tags) Assert.Equal(item.Value, decoded[item.Key]);
        Assert.Equal(["z", "A", "a"], tags.Keys);
        Assert.Null(FileSyncProtocol.BuildTagsHeaderValue(null));
        Assert.Null(FileSyncProtocol.BuildTagsHeaderValue(new Dictionary<string, string>()));
    }
}
