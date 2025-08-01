using System.Text;
using DotNext.IO;
using DotNext.Runtime.Serialization;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListRightPopCommand : ISerializable<ListRightPopCommand>
{
    public const int Id = 4;

    public string Key { get; set; }
    public int Count { get; set; }
    public long NowTicks { get; set; }
    
    public string IdTransaction { get; set; }

    long? IDataTransferObject.Length => Encoding.UTF8.GetByteCount(Key) + sizeof(int) + sizeof(long) + Encoding.UTF8.GetByteCount(IdTransaction);

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var command = this;
        await writer.EncodeAsync(command.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(Count, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);
        await writer.EncodeAsync(command.IdTransaction.AsMemory(), new EncodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<ListRightPopCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var key = await reader.DecodeAsync( new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
        var count = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var nowTicks = await reader.ReadLittleEndianAsync<Int64>(token).ConfigureAwait(false);
        var idTransaction = await reader
            .DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token)
            .ConfigureAwait(false);
        return new ListRightPopCommand
        {
            Key = key.ToString(),
            Count = count,
            NowTicks = nowTicks,
            IdTransaction = idTransaction.ToString()
        };
    }
}