using System.Collections.Immutable;
using System.Text;
using DotNext.IO;
using DotNext.Text;

namespace SlimData.Commands;

internal static class SlimDataSnapshotSerializer
{
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

        var queues = ImmutableDictionary.CreateBuilder<string, ImmutableArray<QueueElement>>();
        var queueCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
        for (var i = 0; i < queueCount; i++)
        {
            var queueKey = await ReadStringAsync(reader, token).ConfigureAwait(false);
            var itemCount = await ReadCountAsync(reader, token).ConfigureAwait(false);
            var queue = ImmutableArray.CreateBuilder<QueueElement>(itemCount);
            for (var itemIndex = 0; itemIndex < itemCount; itemIndex++)
                queue.Add(await ReadQueueElementAsync(reader, token).ConfigureAwait(false));

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

        return new(hashsets.ToImmutable(), keyValues.ToImmutable(), queues.ToImmutable());
    }

    private static async ValueTask<QueueElement> ReadQueueElementAsync<TReader>(
        TReader reader,
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
            tries.Add(new(startTimestamp, transactionId, endTimestamp, httpCode));
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
