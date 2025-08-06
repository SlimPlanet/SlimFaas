using System.Text;
using DotNext.IO;
using DotNext.Runtime.Serialization;
using DotNext.Text;

namespace SlimData.Commands;

public struct DeleteHashSetCommand : ISerializable<DeleteHashSetCommand>
{
    public const int Id = 17;

    public string Key { get; set; }
    public string DictionaryKey { get; set; }

    long? IDataTransferObject.Length // optional implementation, may return null
    {
        get
        {
            // compute length of the serialized data, in bytes
            long result = Encoding.UTF8.GetByteCount(Key);
            result += Encoding.UTF8.GetByteCount(DictionaryKey);

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
        await writer.EncodeAsync(command.DictionaryKey.AsMemory(), context,
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<DeleteHashSetCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token:token).ConfigureAwait(false);
        var dictionaryKey = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token:token).ConfigureAwait(false);
        return new DeleteHashSetCommand
        {
            Key = key.ToString(),
            DictionaryKey = dictionaryKey.ToString()
        };
    }
}