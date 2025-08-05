using System.Text;
using DotNext.IO;
using DotNext.Runtime.Serialization;
using DotNext.Text;

namespace SlimData.Commands;

public struct AddHashSetCommand : ISerializable<AddHashSetCommand>
{
    public const int Id = 1;

    public string Key { get; set; }
    public Dictionary<string, ReadOnlyMemory<byte>> Value { get; set; }

    long? IDataTransferObject.Length // optional implementation, may return null
    {
        get
        {
            // compute length of the serialized data, in bytes
            long result = Encoding.UTF8.GetByteCount(Key);
            result += sizeof(int); // 4 bytes for count
            foreach (var keyValuePair in Value)
                result += Encoding.UTF8.GetByteCount(keyValuePair.Key) + keyValuePair.Value.Length;
            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var command = this;
        var context = new EncodingContext(Encoding.UTF8, true);
        await writer.EncodeAsync(command.Key.AsMemory(), context,
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
        // write the number of entries
        await writer.WriteLittleEndianAsync(Value.Count, token).ConfigureAwait(false);
        // write the entries
        
        foreach (var (key, value) in Value)
        {
            await writer.EncodeAsync(key.AsMemory(), context, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteAsync(value, LengthFormat.Compressed, token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<AddHashSetCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token:token).ConfigureAwait(false);
        var count = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var keysValues = new Dictionary<string, ReadOnlyMemory<byte>>(count);
        // deserialize entries
        var context = new DecodingContext(Encoding.UTF8, true);
        while (count-- > 0)
        {
            var key_ = await reader.DecodeAsync( context, LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
            keysValues.Add(key_.ToString(), value.Memory.ToArray());
        }

        return new AddHashSetCommand
        {
            Key = key.ToString(),
            Value = keysValues
        };
    }
}