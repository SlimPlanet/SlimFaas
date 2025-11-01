using System.Collections.Immutable;
using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Runtime.Serialization;
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

    // Estimation de longueur (même philosophie que ton code initial)
    long? IDataTransferObject.Length
    {
        get
        {
            long result = sizeof(int); // count keyValues
            foreach (var kv in keysValues)
                result += Encoding.UTF8.GetByteCount(kv.Key) + kv.Value.Length;

            result += sizeof(int); // count queues
            foreach (var q in queues)
            {
                result += Encoding.UTF8.GetByteCount(q.Key);
                result += sizeof(int); // queue count
                foreach (var x in q.Value)
                {
                    // Value + Id + Insert + HttpTimeoutSeconds
                    result += x.Value.Length
                           +  Encoding.UTF8.GetByteCount(x.Id)
                           +  sizeof(long)
                           +  sizeof(int);

                    // TimeoutRetriesSeconds (array<int>)
                    result += sizeof(int); // len
                    result += (long)x.TimeoutRetriesSeconds.Length * sizeof(int);

                    // RetryQueueElements
                    result += sizeof(int); // len
                    foreach (var r in x.RetryQueueElements)
                    {
                        result += sizeof(long) * 2 // Start/End
                               +  sizeof(int)      // HttpCode
                               +  Encoding.UTF8.GetByteCount(r.IdTransaction);
                    }

                    // HttpStatusRetries (hashset<int>)
                    result += sizeof(int);
                    result += (long)x.HttpStatusRetries.Count * sizeof(int);
                }
            }

            result += sizeof(int); // count hashsets
            foreach (var hs in hashsets)
            {
                result += Encoding.UTF8.GetByteCount(hs.Key);
                result += sizeof(int); // entries
                foreach (var kv in hs.Value)
                    result += Encoding.UTF8.GetByteCount(kv.Key) + kv.Value.Length;
            }

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        // ---- KeyValues ----
        await writer.WriteLittleEndianAsync(keysValues.Count, token).ConfigureAwait(false);
        foreach (var (key, value) in keysValues)
        {
            await writer.EncodeAsync(key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
        }

        // ---- Queues ----
        await writer.WriteLittleEndianAsync(queues.Count, token).ConfigureAwait(false);
        foreach (var q in queues)
        {
            await writer.EncodeAsync(q.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(q.Value.Count, token).ConfigureAwait(false);

            foreach (var v in q.Value)
            {
                await writer.WriteAsync(v.Value, LengthFormat.Compressed, token).ConfigureAwait(false);
                await writer.EncodeAsync(v.Id.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteBigEndianAsync(v.InsertTimeStamp, token).ConfigureAwait(false);

                // Nouveau champ : HttpTimeoutSeconds
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
                    await writer.EncodeAsync(r.IdTransaction.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                }

                // HttpStatusRetries (hashset)
                await writer.WriteLittleEndianAsync(v.HttpStatusRetries.Count, token).ConfigureAwait(false);
                foreach (var code in v.HttpStatusRetries)
                    await writer.WriteLittleEndianAsync(code, token).ConfigureAwait(false);
            }
        }

        // ---- Hashsets ----
        await writer.WriteLittleEndianAsync(hashsets.Count, token).ConfigureAwait(false);
        foreach (var hs in hashsets)
        {
            await writer.EncodeAsync(hs.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(hs.Value.Count, token).ConfigureAwait(false);

            foreach (var (k, v) in hs.Value)
            {
                await writer.EncodeAsync(k.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
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
            var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
            // IMPORTANT: copier, sinon buffer invalidé après Dispose()
            keysValues.Add(key.ToString(), value.Memory.ToArray());
        }

        // ---- Queues ----
        var queuesCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var queues = new Dictionary<string, List<QueueElement>>(queuesCount);
        while (queuesCount-- > 0)
        {
            var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            var countQueue = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

            var queue = new List<QueueElement>(countQueue);
            while (countQueue-- > 0)
            {
                using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
                var id = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
                var insertTimeStamp = await reader.ReadBigEndianAsync<long>(token).ConfigureAwait(false);

                // Nouveau champ : HttpTimeoutSeconds
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
                    var idTx = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
                    tries[i] = new QueueHttpTryElement(startTs, idTx.ToString(), endTs, httpCode);
                }

                // HttpStatusRetries
                var hsCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                var hs = new int[hsCount];
                for (int i = 0; i < hsCount; i++)
                    hs[i] = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

                // IMPORTANT: copier la payload pour survivre à Dispose()
                var payload = value.Memory.ToArray();

                queue.Add(new QueueElement(
                    payload,
                    id.ToString(),
                    insertTimeStamp,
                    timeoutSeconds,
                    retries.ToImmutableArray(),
                    tries.ToImmutableArray(),
                    hs.ToImmutableHashSet()
                ));
            }

            queues.Add(key.ToString(), queue);
        }

        // ---- Hashsets ----
        var hsTopCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var hashsets = new Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>>(hsTopCount);
        while (hsTopCount-- > 0)
        {
            var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            var countHashset = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

            var hashset = new Dictionary<string, ReadOnlyMemory<byte>>(countHashset);
            while (countHashset-- > 0)
            {
                var hkey = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
                using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
                // idem: copier
                hashset.Add(hkey.ToString(), value.Memory.ToArray());
            }

            hashsets.Add(key.ToString(), hashset);
        }

        return new LogSnapshotCommand(keysValues, hashsets, queues);
    }
}
