using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListRightPopCommand : ICommand<ListRightPopCommand>
{
    public const int Id = 4;
    static int ICommand<ListRightPopCommand>.Id => Id;

    public string Key { get; set; }
    public int Count { get; set; }
    public long NowTicks { get; set; }
    public string IdTransaction { get; set; }

    long? IDataTransferObject.Length
    {
        get
        {
            var key = Key ?? string.Empty;
            var tx = IdTransaction ?? string.Empty;

            // Key: [int32 length][utf8 bytes]
            // Count: int32
            // NowTicks: int64
            // IdTransaction: [int32 length][utf8 bytes]
            long total = 0;
            total += sizeof(int) + Encoding.UTF8.GetByteCount(key);
            total += sizeof(int);
            total += sizeof(long);
            total += sizeof(int) + Encoding.UTF8.GetByteCount(tx);
            return total;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        await writer.EncodeAsync(
                Key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(Count, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);

        await writer.EncodeAsync(
                IdTransaction.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<ListRightPopCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        var count = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var nowTicks = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        using var txOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        return new ListRightPopCommand
        {
            Key = new string(keyOwner.Span),
            Count = count,
            NowTicks = nowTicks,
            IdTransaction = new string(txOwner.Span)
        };
    }
}
