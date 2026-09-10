using System.Collections.Immutable;
using System.Text;
using DotNext.IO;
using DotNext.Text;

namespace SlimData.Commands;

/// <summary>
/// Binary layout of a SlimData Raft snapshot.
/// <para>
/// The <b>body</b> (key/values, queues, hashsets) is the historical, headerless format:
/// it is what every released node reads, and it must stay byte-identical so that
/// snapshots installed by a newer leader remain readable by older followers during a
/// rolling upgrade, and so that a downgrade can still restore its snapshot file.
/// </para>
/// <para>
/// Fields added later are written in a <b>trailer</b> placed after the body. An older
/// reader stops after the hashsets and never sees the trailer; a current reader treats
/// a missing trailer (end of stream right after the body) as a legacy snapshot. The
/// trailer currently carries the reserved IP of every queue try, which the body never
/// stored, so that reserved-IP pinning survives a restart.
/// </para>
/// </summary>
internal static class SlimDataSnapshotSerializer
{
    // Little-endian bytes spell "SLDT" (SlimData Trailer).
    internal const uint TrailerMagic = 0x54444C53U;
    internal const int TrailerVersion = 1;

    internal static async ValueTask WriteAsync<TWriter>(
        TWriter writer,
        SlimDataStateSnapshot snapshot,
        CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var encoding = new EncodingContext(Encoding.UTF8, false);

        await writer.WriteLittleEndianAsync(snapshot.KeyValues.Count, token).ConfigureAwait(false);
        foreach (var (key, value) in snapshot.KeyValues)
        {
            await writer.EncodeAsync(key.AsMemory(), encoding, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
        }

        var tryCount = 0;
        await writer.WriteLittleEndianAsync(snapshot.Queues.Count, token).ConfigureAwait(false);
        foreach (var (queueKey, queue) in snapshot.Queues)
        {
            await writer.EncodeAsync(queueKey.AsMemory(), encoding, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(queue.Length, token).ConfigureAwait(false);

            foreach (var item in queue)
            {
                await writer.WriteAsync(item.Value, LengthFormat.Compressed, token).ConfigureAwait(false);
                await writer.EncodeAsync(item.Id.AsMemory(), encoding, LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteBigEndianAsync(item.InsertTimeStamp, token).ConfigureAwait(false);
                await writer.WriteLittleEndianAsync(item.HttpTimeoutSeconds, token).ConfigureAwait(false);

                await writer.WriteLittleEndianAsync(item.TimeoutRetriesSeconds.Length, token).ConfigureAwait(false);
                foreach (var retry in item.TimeoutRetriesSeconds)
                    await writer.WriteLittleEndianAsync(retry, token).ConfigureAwait(false);

                await writer.WriteLittleEndianAsync(item.RetryQueueElements.Length, token).ConfigureAwait(false);
                foreach (var retry in item.RetryQueueElements)
                {
                    await writer.WriteBigEndianAsync(retry.StartTimeStamp, token).ConfigureAwait(false);
                    await writer.WriteBigEndianAsync(retry.EndTimeStamp, token).ConfigureAwait(false);
                    await writer.WriteLittleEndianAsync(retry.HttpCode, token).ConfigureAwait(false);
                    await writer.EncodeAsync(retry.IdTransaction.AsMemory(), encoding, LengthFormat.LittleEndian, token)
                        .ConfigureAwait(false);
                    tryCount++;
                }

                await writer.WriteLittleEndianAsync(item.HttpStatusRetries.Count, token).ConfigureAwait(false);
                foreach (var statusCode in item.HttpStatusRetries)
                    await writer.WriteLittleEndianAsync(statusCode, token).ConfigureAwait(false);
            }
        }

        await writer.WriteLittleEndianAsync(snapshot.Hashsets.Count, token).ConfigureAwait(false);
        foreach (var (hashsetKey, hashset) in snapshot.Hashsets)
        {
            await writer.EncodeAsync(hashsetKey.AsMemory(), encoding, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(hashset.Count, token).ConfigureAwait(false);
            foreach (var (key, value) in hashset)
            {
                await writer.EncodeAsync(key.AsMemory(), encoding, LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
            }
        }

        // Trailer: the reserved IP of every try, in the exact order the body wrote them.
        // Both walks enumerate the same immutable snapshot, so the order is identical.
        await writer.WriteLittleEndianAsync(TrailerMagic, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(TrailerVersion, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(tryCount, token).ConfigureAwait(false);
        foreach (var queue in snapshot.Queues.Values)
        foreach (var item in queue)
        foreach (var retry in item.RetryQueueElements)
        {
            await writer.EncodeAsync((retry.ReservedIp ?? string.Empty).AsMemory(), encoding, LengthFormat.LittleEndian, token)
                .ConfigureAwait(false);
        }
    }

    internal static async ValueTask<SlimDataStateSnapshot> ReadAsync<TReader>(
        TReader reader,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        var keyValues = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
        var keyValueCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        for (var i = 0; i < keyValueCount; i++)
        {
            var key = await ReadStringAsync(reader, token).ConfigureAwait(false);
            using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
            keyValues.Add(key, value.Memory.ToArray());
        }

        var tries = new List<QueueHttpTryElement>();
        var queues = ImmutableDictionary.CreateBuilder<string, ImmutableArray<QueueElement>>();
        var queueCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        for (var i = 0; i < queueCount; i++)
        {
            var queueKey = await ReadStringAsync(reader, token).ConfigureAwait(false);
            var itemCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
            var queue = ImmutableArray.CreateBuilder<QueueElement>(itemCount);
            for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                queue.Add(await ReadQueueElementAsync(reader, tries, token).ConfigureAwait(false));

            queues.Add(queueKey, queue.MoveToImmutable());
        }

        var hashsets = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>();
        var hashsetCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        for (var i = 0; i < hashsetCount; i++)
        {
            var hashsetKey = await ReadStringAsync(reader, token).ConfigureAwait(false);
            var itemCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
            var hashset = ImmutableDictionary.CreateBuilder<string, ReadOnlyMemory<byte>>();
            for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
            {
                var key = await ReadStringAsync(reader, token).ConfigureAwait(false);
                using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
                hashset.Add(key, value.Memory.ToArray());
            }

            hashsets.Add(hashsetKey, hashset.ToImmutable());
        }

        await ReadTrailerAsync(reader, tries, token).ConfigureAwait(false);

        return new(hashsets.ToImmutable(), keyValues.ToImmutable(), queues.ToImmutable());
    }

    private static async ValueTask<QueueElement> ReadQueueElementAsync<TReader>(
        TReader reader,
        List<QueueHttpTryElement> allTries,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
        var id = await ReadStringAsync(reader, token).ConfigureAwait(false);
        var insertTimestamp = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);
        var timeoutSeconds = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

        var retryCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        var retries = ImmutableArray.CreateBuilder<int>(retryCount);
        for (var i = 0; i < retryCount; i++)
            retries.Add(await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false));

        var tryCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        var tries = ImmutableArray.CreateBuilder<QueueHttpTryElement>(tryCount);
        for (var i = 0; i < tryCount; i++)
        {
            var startTimestamp = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);
            var endTimestamp = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);
            var httpCode = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
            var transactionId = await ReadStringAsync(reader, token).ConfigureAwait(false);
            var attempt = new QueueHttpTryElement(startTimestamp, transactionId, endTimestamp, httpCode);
            tries.Add(attempt);
            allTries.Add(attempt);
        }

        var statusCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        var statuses = ImmutableHashSet.CreateBuilder<int>();
        for (var i = 0; i < statusCount; i++)
            statuses.Add(await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false));

        return new(
            value.Memory.ToArray(),
            id,
            insertTimestamp,
            timeoutSeconds,
            retries.MoveToImmutable(),
            tries.MoveToImmutable(),
            statuses.ToImmutable());
    }

    /// <summary>
    /// Reads the trailer when present. A stream ending right after the body is a legacy
    /// snapshot (tries keep an empty reserved IP); anything else that is not a valid
    /// trailer is corruption.
    /// </summary>
    private static async ValueTask ReadTrailerAsync<TReader>(
        TReader reader,
        List<QueueHttpTryElement> tries,
        CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        uint magic;
        try
        {
            magic = await reader.ReadLittleEndianAsync<uint>(token).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            return;
        }

        if (magic != TrailerMagic)
            throw new InvalidDataException($"Unexpected data 0x{magic:X8} after the SlimData snapshot body.");

        var version = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        if (version != TrailerVersion)
            throw new InvalidDataException($"Unsupported SlimData snapshot trailer version {version}.");

        var count = await ReadCountAsync(reader, token).ConfigureAwait(false);
        if (count != tries.Count)
        {
            throw new InvalidDataException(
                $"SlimData snapshot trailer describes {count} queue tries but the body holds {tries.Count}.");
        }

        for (var i = 0; i < count; i++)
            tries[i].ReservedIp = await ReadStringAsync(reader, token).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadCountAsync<TReader>(TReader reader, CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        var count = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        return count >= 0 ? count : throw new InvalidDataException($"Negative collection count {count} in SlimData snapshot.");
    }

    private static async ValueTask<string> ReadStringAsync<TReader>(TReader reader, CancellationToken token)
        where TReader : notnull, IAsyncBinaryReader
    {
        using var owner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);
        return new(owner.Span);
    }
}
