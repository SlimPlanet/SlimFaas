using System.Collections.Immutable;
using System.Text;
using DotNext.IO;
using DotNext.Text;
using SlimData.Commands;

namespace SlimData.Tests;

/// <summary>
/// The snapshot body must stay byte-identical to the historical format (older nodes read
/// it and ignore what follows); the reserved IP of each queue try travels in a trailer
/// that older readers never reach and that current readers treat as optional.
/// </summary>
public sealed class SlimDataSnapshotSerializerTests
{
    private static readonly long Now = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public async Task Round_trip_restores_the_reserved_ip_of_every_try()
    {
        var snapshot = CreateSnapshot();

        var restored = await ReadAsync(await WriteAsync(snapshot));

        var first = restored.Queues["queue-a"];
        Assert.Equal(["a-1", "a-2", "a-3"], first.Select(e => e.Id));
        Assert.Equal(["10.0.0.1", "10.0.0.2"], first[0].RetryQueueElements.Select(t => t.ReservedIp));
        Assert.Empty(first[1].RetryQueueElements);
        Assert.Equal([""], first[2].RetryQueueElements.Select(t => t.ReservedIp));
        Assert.Equal(["10.0.1.9"], restored.Queues["queue-b"][0].RetryQueueElements.Select(t => t.ReservedIp));

        // The rest of the try is intact.
        var attempt = first[0].RetryQueueElements[1];
        Assert.Equal("tx-a-1-2", attempt.IdTransaction);
        Assert.Equal(Now + 2, attempt.StartTimeStamp);
        Assert.Equal(Now + 3, attempt.EndTimeStamp);
        Assert.Equal(503, attempt.HttpCode);

        Assert.Equal("value", Encoding.UTF8.GetString(restored.KeyValues["key"].Span));
        Assert.Equal("field-value", Encoding.UTF8.GetString(restored.Hashsets["hash"]["field"].Span));
    }

    [Fact]
    public async Task Body_is_byte_identical_to_the_legacy_format_so_older_readers_ignore_the_trailer()
    {
        var snapshot = CreateSnapshot();

        var current = await WriteAsync(snapshot);
        var legacy = await WriteLegacyAsync(snapshot);

        Assert.True(current.Length > legacy.Length);
        Assert.Equal(legacy, current[..legacy.Length]);
        Assert.Equal(SlimDataSnapshotSerializer.TrailerMagic, BitConverter.ToUInt32(current, legacy.Length));
    }

    [Fact]
    public async Task Legacy_snapshot_without_trailer_is_restored_with_empty_reserved_ips()
    {
        var snapshot = CreateSnapshot();

        var restored = await ReadAsync(await WriteLegacyAsync(snapshot));

        var first = restored.Queues["queue-a"];
        Assert.Equal(["a-1", "a-2", "a-3"], first.Select(e => e.Id));
        Assert.Equal(["", ""], first[0].RetryQueueElements.Select(t => t.ReservedIp));
        Assert.Equal("tx-a-1-2", first[0].RetryQueueElements[1].IdTransaction);
        Assert.Equal("value", Encoding.UTF8.GetString(restored.KeyValues["key"].Span));
        Assert.Equal("field-value", Encoding.UTF8.GetString(restored.Hashsets["hash"]["field"].Span));
    }

    [Fact]
    public async Task Empty_snapshot_round_trips()
    {
        var restored = await ReadAsync(await WriteAsync(SlimDataStateSnapshot.Empty));

        Assert.Empty(restored.KeyValues);
        Assert.Empty(restored.Queues);
        Assert.Empty(restored.Hashsets);
    }

    [Fact]
    public async Task Truncated_trailer_is_rejected()
    {
        var bytes = await WriteAsync(CreateSnapshot());

        await Assert.ThrowsAsync<EndOfStreamException>(() => ReadAsync(bytes[..^3]).AsTask());
    }

    [Fact]
    public async Task Trailer_with_unknown_magic_is_rejected()
    {
        var snapshot = CreateSnapshot();
        var bytes = (await WriteLegacyAsync(snapshot)).Concat(Encoding.ASCII.GetBytes("JUNK")).ToArray();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(bytes).AsTask());
        Assert.Contains("after the SlimData snapshot body", error.Message);
    }

    [Fact]
    public async Task Trailer_with_mismatched_try_count_is_rejected()
    {
        var snapshot = CreateSnapshot();
        var bytes = (await WriteLegacyAsync(snapshot))
            .Concat(BitConverter.GetBytes(SlimDataSnapshotSerializer.TrailerMagic))
            .Concat(BitConverter.GetBytes(SlimDataSnapshotSerializer.TrailerVersion))
            .Concat(BitConverter.GetBytes(1))
            .ToArray();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(bytes).AsTask());
        Assert.Contains("describes 1 queue tries but the body holds 4", error.Message);
    }

    [Fact]
    public async Task Trailer_with_unsupported_version_is_rejected()
    {
        var snapshot = CreateSnapshot();
        var bytes = (await WriteLegacyAsync(snapshot))
            .Concat(BitConverter.GetBytes(SlimDataSnapshotSerializer.TrailerMagic))
            .Concat(BitConverter.GetBytes(SlimDataSnapshotSerializer.TrailerVersion + 1))
            .ToArray();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(bytes).AsTask());
        Assert.Contains("trailer version", error.Message);
    }

    // ---------------------------------------------------------------- helpers

    private static SlimDataStateSnapshot CreateSnapshot()
    {
        var queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
            .Add("queue-a",
            [
                Element("a-1",
                    new QueueHttpTryElement(Now, "tx-a-1-1", Now + 1, 500, "10.0.0.1"),
                    new QueueHttpTryElement(Now + 2, "tx-a-1-2", Now + 3, 503, "10.0.0.2")),
                Element("a-2"),
                Element("a-3", new QueueHttpTryElement(Now + 4, "tx-a-3-1")),
            ])
            .Add("queue-b", [Element("b-1", new QueueHttpTryElement(Now + 5, "tx-b-1-1", reservedIp: "10.0.1.9"))]);

        return new SlimDataStateSnapshot(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add("hash", ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                    .Add("field", Encoding.UTF8.GetBytes("field-value"))),
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty
                .Add("key", Encoding.UTF8.GetBytes("value")),
            queues);
    }

    private static QueueElement Element(string id, params QueueHttpTryElement[] tries) => new(
        Encoding.UTF8.GetBytes($"payload-{id}"),
        id,
        Now - TimeSpan.TicksPerSecond,
        httpTimeoutSeconds: 30,
        [2, 4],
        [.. tries],
        [500, 503]);

    private static async Task<byte[]> WriteAsync(SlimDataStateSnapshot snapshot)
    {
        await using var stream = new MemoryStream();
        var writer = IAsyncBinaryWriter.Create(stream, new byte[256]);
        await SlimDataSnapshotSerializer.WriteAsync(writer, snapshot, CancellationToken.None);
        await stream.FlushAsync();
        return stream.ToArray();
    }

    private static async ValueTask<SlimDataStateSnapshot> ReadAsync(byte[] bytes)
    {
        await using var stream = new MemoryStream(bytes);
        var reader = IAsyncBinaryReader.Create(stream, new byte[256]);
        return await SlimDataSnapshotSerializer.ReadAsync(reader, CancellationToken.None);
    }

    /// <summary>Byte-for-byte replica of the snapshot format written by releases up to 0.84.</summary>
    private static async Task<byte[]> WriteLegacyAsync(SlimDataStateSnapshot snapshot)
    {
        await using var stream = new MemoryStream();
        var writer = IAsyncBinaryWriter.Create(stream, new byte[256]);
        var encoding = new EncodingContext(Encoding.UTF8, false);
        var token = CancellationToken.None;

        await writer.WriteLittleEndianAsync(snapshot.KeyValues.Count, token);
        foreach (var (key, value) in snapshot.KeyValues)
        {
            await writer.EncodeAsync(key.AsMemory(), encoding, LengthFormat.LittleEndian, token);
            await writer.WriteAsync(value, LengthFormat.Compressed, token);
        }

        await writer.WriteLittleEndianAsync(snapshot.Queues.Count, token);
        foreach (var (queueKey, queue) in snapshot.Queues)
        {
            await writer.EncodeAsync(queueKey.AsMemory(), encoding, LengthFormat.LittleEndian, token);
            await writer.WriteLittleEndianAsync(queue.Length, token);
            foreach (var item in queue)
            {
                await writer.WriteAsync(item.Value, LengthFormat.Compressed, token);
                await writer.EncodeAsync(item.Id.AsMemory(), encoding, LengthFormat.LittleEndian, token);
                await writer.WriteBigEndianAsync(item.InsertTimeStamp, token);
                await writer.WriteLittleEndianAsync(item.HttpTimeoutSeconds, token);
                await writer.WriteLittleEndianAsync(item.TimeoutRetriesSeconds.Length, token);
                foreach (var retry in item.TimeoutRetriesSeconds)
                    await writer.WriteLittleEndianAsync(retry, token);
                await writer.WriteLittleEndianAsync(item.RetryQueueElements.Length, token);
                foreach (var retry in item.RetryQueueElements)
                {
                    await writer.WriteBigEndianAsync(retry.StartTimeStamp, token);
                    await writer.WriteBigEndianAsync(retry.EndTimeStamp, token);
                    await writer.WriteLittleEndianAsync(retry.HttpCode, token);
                    await writer.EncodeAsync(retry.IdTransaction.AsMemory(), encoding, LengthFormat.LittleEndian, token);
                }
                await writer.WriteLittleEndianAsync(item.HttpStatusRetries.Count, token);
                foreach (var statusCode in item.HttpStatusRetries)
                    await writer.WriteLittleEndianAsync(statusCode, token);
            }
        }

        await writer.WriteLittleEndianAsync(snapshot.Hashsets.Count, token);
        foreach (var (hashsetKey, hashset) in snapshot.Hashsets)
        {
            await writer.EncodeAsync(hashsetKey.AsMemory(), encoding, LengthFormat.LittleEndian, token);
            await writer.WriteLittleEndianAsync(hashset.Count, token);
            foreach (var (key, value) in hashset)
            {
                await writer.EncodeAsync(key.AsMemory(), encoding, LengthFormat.LittleEndian, token);
                await writer.WriteAsync(value, LengthFormat.Compressed, token);
            }
        }

        await stream.FlushAsync();
        return stream.ToArray();
    }
}
