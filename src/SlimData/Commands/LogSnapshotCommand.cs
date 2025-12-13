using System.Collections.Immutable;
using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public readonly struct LogSnapshotCommand(
    Dictionary<string, ReadOnlyMemory<byte>> keysValues,
    Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>> hashsets,
    Dictionary<string, List<QueueElement>> queues // on garde List ici pour I/O, tu convertiras en ImmutableArray côté state
) : ICommand<LogSnapshotCommand>
{
    public const int Id = 5;

    static int ICommand<LogSnapshotCommand>.Id => Id;
    static bool ICommand<LogSnapshotCommand>.IsSnapshot => true;

    public readonly Dictionary<string, ReadOnlyMemory<byte>> keysValues = keysValues;
    public readonly Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>> hashsets = hashsets;
    public readonly Dictionary<string, List<QueueElement>> queues = queues;

    // Estimation de longueur *exacte* (inclut les préfixes ajoutés par EncodeAsync et WriteAsync(Compressed))
    long? IDataTransferObject.Length
    {
        get
        {
            long result = 0;

            // ---- KeyValues ----
            result += sizeof(int); // count keyValues
            if (this.keysValues is not null)
            {
                foreach (var kv in this.keysValues)
                {
                    // key: [int32 length][utf8 bytes]
                    result += sizeof(int) + Encoding.UTF8.GetByteCount(kv.Key ?? string.Empty);

                    // value: [7-bit length][bytes]
                    int valueLen = kv.Value.Length;
                    result += Get7BitEncodedIntSize(valueLen) + valueLen;
                }
            }

            // ---- Queues ----
            result += sizeof(int); // count queues
            if (this.queues is not null)
            {
                foreach (var q in this.queues)
                {
                    // queue key: [int32 length][utf8 bytes]
                    result += sizeof(int) + Encoding.UTF8.GetByteCount(q.Key ?? string.Empty);

                    // queue count
                    result += sizeof(int);

                    foreach (var x in q.Value)
                    {
                        // Value: [7-bit length][bytes]
                        int payloadLen = x.Value.Length;
                        result += Get7BitEncodedIntSize(payloadLen) + payloadLen;

                        // Id: [int32 length][utf8 bytes]
                        result += sizeof(int) + Encoding.UTF8.GetByteCount(x.Id ?? string.Empty);

                        // Insert timestamp (BigEndian long) => 8 bytes
                        result += sizeof(long);

                        // HttpTimeoutSeconds (int32)
                        result += sizeof(int);

                        // TimeoutRetriesSeconds: [int32 len][int32...]
                        result += sizeof(int) + (long)x.TimeoutRetriesSeconds.Length * sizeof(int);

                        // RetryQueueElements: [int32 len][...]
                        result += sizeof(int);
                        foreach (var r in x.RetryQueueElements)
                        {
                            result += sizeof(long); // Start
                            result += sizeof(long); // End
                            result += sizeof(int);  // HttpCode

                            // IdTransaction: [int32 length][utf8 bytes]
                            result += sizeof(int) + Encoding.UTF8.GetByteCount(r.IdTransaction ?? string.Empty);
                        }

                        // HttpStatusRetries (hashset<int>): [int32 count][int32...]
                        result += sizeof(int) + (long)x.HttpStatusRetries.Count * sizeof(int);
                    }
                }
            }

            // ---- Hashsets ----
            result += sizeof(int); // count hashsets
            if (this.hashsets is not null)
            {
                foreach (var hs in this.hashsets)
                {
                    // hashset key: [int32 length][utf8 bytes]
                    result += sizeof(int) + Encoding.UTF8.GetByteCount(hs.Key ?? string.Empty);

                    // entries count
                    result += sizeof(int);

                    foreach (var kv in hs.Value)
                    {
                        // entry key: [int32 length][utf8 bytes]
                        result += sizeof(int) + Encoding.UTF8.GetByteCount(kv.Key ?? string.Empty);

                        // entry value: [7-bit length][bytes]
                        int entryLen = kv.Value.Length;
                        result += Get7BitEncodedIntSize(entryLen) + entryLen;
                    }
                }
            }

            return result;
        }
    }

    private static int Get7BitEncodedIntSize(int value)
    {
        uint v = (uint)value;
        int size = 1;
        while (v >= 0x80)
        {
            size++;
            v >>= 7;
        }
        return size;
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var enc = new EncodingContext(Encoding.UTF8, false);

        // ---- KeyValues ----
        await writer.WriteLittleEndianAsync(keysValues.Count, token).ConfigureAwait(false);
        foreach (var (key, value) in keysValues)
        {
            await writer.EncodeAsync(key.AsMemory(), enc, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
        }

        // ---- Queues ----
        await writer.WriteLittleEndianAsync(queues.Count, token).ConfigureAwait(false);
        foreach (var q in queues)
        {
            await writer.EncodeAsync(q.Key.AsMemory(), enc, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(q.Value.Count, token).ConfigureAwait(false);

            foreach (var v in q.Value)
            {
                await writer.WriteAsync(v.Value, LengthFormat.Compressed, token).ConfigureAwait(false);
                await writer.EncodeAsync(v.Id.AsMemory(), enc, LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteBigEndianAsync(v.InsertTimeStamp, token).ConfigureAwait(false);

                // HttpTimeoutSeconds
                await writer.WriteLittleEndianAsync(v.HttpTimeoutSeconds, token).ConfigureAwait(false);

                // TimeoutRetriesSeconds
                await writer.WriteLittleEndianAsync(v.TimeoutRetriesSeconds.Length, token).ConfigureAwait(false);
                for (int i = 0; i < v.TimeoutRetriesSeconds.Length; i++)
                    await writer.WriteLittleEndianAsync(v.TimeoutRetriesSeconds[i], token).ConfigureAwait(false);

                // RetryQueueElements
                await writer.WriteLittleEndianAsync(v.RetryQueueElements.Length, token).ConfigureAwait(false);
                for (int i = 0; i < v.RetryQueueElements.Length; i++)
                {
                    var r = v.RetryQueueElements[i];
                    await writer.WriteBigEndianAsync(r.StartTimeStamp, token).ConfigureAwait(false);
                    await writer.WriteBigEndianAsync(r.EndTimeStamp, token).ConfigureAwait(false);
                    await writer.WriteLittleEndianAsync(r.HttpCode, token).ConfigureAwait(false);
                    await writer.EncodeAsync(r.IdTransaction.AsMemory(), enc, LengthFormat.LittleEndian, token)
                        .ConfigureAwait(false);
                }

                // HttpStatusRetries
                await writer.WriteLittleEndianAsync(v.HttpStatusRetries.Count, token).ConfigureAwait(false);
                foreach (var code in v.HttpStatusRetries)
                    await writer.WriteLittleEndianAsync(code, token).ConfigureAwait(false);
            }
        }

        // ---- Hashsets ----
        await writer.WriteLittleEndianAsync(hashsets.Count, token).ConfigureAwait(false);
        foreach (var hs in hashsets)
        {
            await writer.EncodeAsync(hs.Key.AsMemory(), enc, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(hs.Value.Count, token).ConfigureAwait(false);

            foreach (var (k, v) in hs.Value)
            {
                await writer.EncodeAsync(k.AsMemory(), enc, LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteAsync(v, LengthFormat.Compressed, token).ConfigureAwait(false);
            }
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<LogSnapshotCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        // ---- KeyValues ----
        var kvCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var keysValues = new Dictionary<string, ReadOnlyMemory<byte>>(kvCount);

        while (kvCount-- > 0)
        {
            using var keyOwner = await reader.DecodeAsync(
                new DecodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token: token).ConfigureAwait(false);

            using var valueOwner = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);

            keysValues.Add(new string(keyOwner.Span), valueOwner.Memory.ToArray());
        }

        // ---- Queues ----
        var queuesCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var queues = new Dictionary<string, List<QueueElement>>(queuesCount);

        while (queuesCount-- > 0)
        {
            using var queueKeyOwner = await reader.DecodeAsync(
                new DecodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token: token).ConfigureAwait(false);

            string queueKey = new string(queueKeyOwner.Span);

            var countQueue = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
            var queue = new List<QueueElement>(countQueue);

            while (countQueue-- > 0)
            {
                using var valueOwner = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);

                using var idOwner = await reader.DecodeAsync(
                    new DecodingContext(Encoding.UTF8, false),
                    LengthFormat.LittleEndian,
                    token: token).ConfigureAwait(false);

                string id = new string(idOwner.Span);

                var insertTimeStamp = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);

                // HttpTimeoutSeconds
                var timeoutSeconds = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

                // TimeoutRetriesSeconds
                var retriesLen = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                var retries = new int[retriesLen];
                for (int i = 0; i < retriesLen; i++)
                    retries[i] = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

                // RetryQueueElements
                var tryCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                var tries = new QueueHttpTryElement[tryCount];
                for (int i = 0; i < tryCount; i++)
                {
                    var startTs = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);
                    var endTs = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);
                    var httpCode = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

                    using var idTxOwner = await reader.DecodeAsync(
                        new DecodingContext(Encoding.UTF8, false),
                        LengthFormat.LittleEndian,
                        token: token).ConfigureAwait(false);

                    string idTx = new string(idTxOwner.Span);

                    tries[i] = new QueueHttpTryElement(startTs, idTx, endTs, httpCode);
                }

                // HttpStatusRetries
                var hsCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                var hs = new int[hsCount];
                for (int i = 0; i < hsCount; i++)
                    hs[i] = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

                var payload = valueOwner.Memory.ToArray();

                queue.Add(new QueueElement(
                    payload,
                    id,
                    insertTimeStamp,
                    timeoutSeconds,
                    retries.ToImmutableArray(),
                    tries.ToImmutableArray(),
                    hs.ToImmutableHashSet()
                ));
            }

            queues.Add(queueKey, queue);
        }

        // ---- Hashsets ----
        var hsTopCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var hashsets = new Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>>(hsTopCount);

        while (hsTopCount-- > 0)
        {
            using var hsKeyOwner = await reader.DecodeAsync(
                new DecodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token: token).ConfigureAwait(false);

            string hsKey = new string(hsKeyOwner.Span);

            var countHashset = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
            var hashset = new Dictionary<string, ReadOnlyMemory<byte>>(countHashset);

            while (countHashset-- > 0)
            {
                using var entryKeyOwner = await reader.DecodeAsync(
                    new DecodingContext(Encoding.UTF8, false),
                    LengthFormat.LittleEndian,
                    token: token).ConfigureAwait(false);

                using var valueOwner = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);

                hashset.Add(new string(entryKeyOwner.Span), valueOwner.Memory.ToArray());
            }

            hashsets.Add(hsKey, hashset);
        }

        return new LogSnapshotCommand(keysValues, hashsets, queues);
    }
}
