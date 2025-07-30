using System.Text;
using DotNext.IO;
using DotNext.Runtime.Serialization;
using DotNext.Text;

namespace SlimData.Commands;

public readonly struct LogSnapshotCommand(Dictionary<string, ReadOnlyMemory<byte>> keysValues,
        Dictionary<string, Dictionary<string, string>> hashsets, Dictionary<string, List<QueueElement>> queues)
    : ISerializable<LogSnapshotCommand>
{
    public const int Id = 5;

    public readonly Dictionary<string, ReadOnlyMemory<byte>> keysValues = keysValues;
    public readonly Dictionary<string, Dictionary<string, string>> hashsets = hashsets;
    public readonly Dictionary<string, List<QueueElement>> queues = queues;


    long? IDataTransferObject.Length // optional implementation, may return null
    {
        get
        {
            // compute length of the serialized data, in bytes
            long result = sizeof(Int32); // 4 bytes for count
            foreach (var keyValuePair in keysValues)
                result += Encoding.UTF8.GetByteCount(keyValuePair.Key) + keyValuePair.Value.Length;

            // compute length of the serialized data, in bytes
            result += sizeof(Int32);
            foreach (var queue in queues)
            {
                result += Encoding.UTF8.GetByteCount(queue.Key);
                result += sizeof(Int32); // 4 bytes for queue count
                queue.Value.ForEach(x =>
                {
                    result += x.Value.Length + Encoding.UTF8.GetByteCount(x.Id) + sizeof(Int64);
                    result += sizeof(Int32); // 4 bytes for timeout
                    result += sizeof(Int32); // 4 bytes for retries count
                    result += x.TimeoutRetries.Count * sizeof(Int32);
                    
                    result += sizeof(Int32); // 4 bytes for hashset count
                    foreach (var retryQueueElement in x.RetryQueueElements)
                    {
                        result += sizeof(Int64) * 2 + sizeof(Int32);
                    }
                    
                    result += sizeof(Int32); // 4 bytes for HttpStatusRetries count
                    result += x.HttpStatusRetries.Count * sizeof(Int32);
                });
            }

            // compute length of the serialized data, in bytes
            result += sizeof(Int32);
            foreach (var hashset in hashsets)
            {
                result += Encoding.UTF8.GetByteCount(hashset.Key);
                result += sizeof(Int32); // 4 bytes for hashset count
                foreach (var keyValuePair in hashset.Value)
                    result += Encoding.UTF8.GetByteCount(keyValuePair.Key) +
                              Encoding.UTF8.GetByteCount(keyValuePair.Value);
            }
            return result;
        }
    }


    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
            // write the number of entries
            await writer.WriteLittleEndianAsync(keysValues.Count, token).ConfigureAwait(false);
            // write the entries
            foreach (var (key, value) in keysValues)
            {
                await writer.EncodeAsync(key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
            }

            // write the number of entries
            await writer.WriteLittleEndianAsync(queues.Count, token).ConfigureAwait(false);
            // write the entries
            foreach (var queue in queues)
            {
                await writer.EncodeAsync(queue.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteLittleEndianAsync(queue.Value.Count, token).ConfigureAwait(false);
                foreach (var value in queue.Value){
                    await writer.WriteAsync(value.Value, LengthFormat.Compressed, token).ConfigureAwait(false);
                    await writer.EncodeAsync(value.Id.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                    await writer.WriteBigEndianAsync(value.InsertTimeStamp,  token).ConfigureAwait(false);
                    await writer.WriteLittleEndianAsync(value.HttpTimeout, token).ConfigureAwait(false);
                    await writer.WriteLittleEndianAsync(value.TimeoutRetries.Count, token).ConfigureAwait(false);
                    foreach (var valueRetry in value.TimeoutRetries)
                    {
                        await writer.WriteLittleEndianAsync(valueRetry, token).ConfigureAwait(false);
                    }
                    
                    await writer.WriteLittleEndianAsync(value.RetryQueueElements.Count, token).ConfigureAwait(false);
                    foreach (var retryQueueElement in value.RetryQueueElements)
                    {
                        await writer.WriteBigEndianAsync(retryQueueElement.StartTimeStamp, token).ConfigureAwait(false);
                        await writer.WriteBigEndianAsync(retryQueueElement.EndTimeStamp, token).ConfigureAwait(false);
                        await writer.WriteLittleEndianAsync(retryQueueElement.HttpCode, token).ConfigureAwait(false);
                        await writer.EncodeAsync(retryQueueElement.IdTransaction.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);

                    }
                    
                    await writer.WriteLittleEndianAsync(value.HttpStatusRetries.Count, token).ConfigureAwait(false);
                    foreach (var httpStatusCode in value.HttpStatusRetries)
                    {
                        await writer.WriteLittleEndianAsync(httpStatusCode, token).ConfigureAwait(false);
                    }
                }
            }

            // write the number of entries
            await writer.WriteLittleEndianAsync(hashsets.Count, token).ConfigureAwait(false);
            // write the entries
            foreach (var hashset in hashsets)
            {
                await writer.EncodeAsync(hashset.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                await writer.WriteLittleEndianAsync(hashset.Value.Count, token).ConfigureAwait(false);
                foreach (var (key, value) in hashset.Value)
                {
                    await writer.EncodeAsync(key.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                    await writer.EncodeAsync(value.AsMemory(), new EncodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token).ConfigureAwait(false);
                }
            }
    }

#pragma warning disable CA2252
    public static async ValueTask<LogSnapshotCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {

            var count = await reader.ReadLittleEndianAsync<Int32>(token);
            var keysValues = new Dictionary<string,ReadOnlyMemory<byte>>(count);
            // deserialize entries;
            while (count-- > 0)
            {
                var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                    .ConfigureAwait(false);
                using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
                keysValues.Add(key.ToString(), value.Memory);
            }

            var countQueues = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
            var queues = new Dictionary<string, List<QueueElement>>(countQueues);
            // deserialize entries
            while (countQueues-- > 0)
            {
                var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                    .ConfigureAwait(false);
                var countQueue = await reader.ReadLittleEndianAsync<Int32>(token);
                var queue = new List<QueueElement>(countQueue);
                while (countQueue-- > 0)
                {
                    using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
                    var id = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                        .ConfigureAwait(false);
                    var insertTimeStamp = await reader.ReadBigEndianAsync<Int64>(token);
                    var timeout = await reader.ReadLittleEndianAsync<Int32>(token);
                    var countRetries = await reader.ReadLittleEndianAsync<Int32>(token);
                    var retries = new List<int>(countRetries);
                    while (countRetries-- > 0)
                    {
                        retries.Add(await reader.ReadLittleEndianAsync<Int32>(token));
                    }
                    
                    var countRetryQueueElements = await reader.ReadLittleEndianAsync<Int32>(token);
                    var retryQueueElements = new List<QueueHttpTryElement>(countRetryQueueElements);
                    while (countRetryQueueElements-- > 0)
                    {
                        var startTimestamp = await reader.ReadBigEndianAsync<Int64>(token);
                        var endTimestamp = await reader.ReadBigEndianAsync<Int64>(token);
                        var httpCode = await reader.ReadLittleEndianAsync<Int32>(token);
                        var idTransaction = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                            .ConfigureAwait(false);
                        retryQueueElements.Add(new QueueHttpTryElement(startTimestamp, idTransaction.ToString(), endTimestamp, httpCode));
                    }
                    
                    var countHttpStatusCodesWorthRetrying = await reader.ReadLittleEndianAsync<Int32>(token);
                    var httpStatusCodesWorthRetrying = new List<int>(countHttpStatusCodesWorthRetrying);
                    while (countHttpStatusCodesWorthRetrying-- > 0)
                    {
                        httpStatusCodesWorthRetrying.Add(await reader.ReadLittleEndianAsync<Int32>(token));
                    }
                    
                    queue.Add(new QueueElement(value.Memory, id.ToString(), insertTimeStamp, timeout, retries, retryQueueElements, httpStatusCodesWorthRetrying));
                }

                queues.Add(key.ToString(), queue);
            }

            var countHashsets = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
            var hashsets = new Dictionary<string, Dictionary<string, string>>(countHashsets);
            // deserialize entries
            while (countHashsets-- > 0)
            {
                var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                    .ConfigureAwait(false);
                var countHashset = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
                var hashset = new Dictionary<string, string>(countHashset);
                while (countHashset-- > 0)
                {
                    var keyHashset = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                        .ConfigureAwait(false);
                    var valueHashset = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
                        .ConfigureAwait(false);
                    hashset.Add(keyHashset.ToString(), valueHashset.ToString());
                }

                hashsets.Add(key.ToString(), hashset);
            }
            return new LogSnapshotCommand(keysValues, hashsets, queues);
    }
}