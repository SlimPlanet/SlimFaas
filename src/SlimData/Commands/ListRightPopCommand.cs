using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListRightPopCommand : ICommand<ListRightPopCommand>
{
    public const int Id = 19;
    static int ICommand<ListRightPopCommand>.Id => Id;

    public string Key { get; set; }
    public int Count { get; set; }
    public long NowTicks { get; set; }
    public string IdTransaction { get; set; }
    public List<string> ReservedIps { get; set; }

    long? IDataTransferObject.Length => null;

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

        var reservedIpsCount = ReservedIps?.Count ?? 0;
        await writer.WriteLittleEndianAsync(reservedIpsCount, token).ConfigureAwait(false);
        if (reservedIpsCount > 0)
        {
            foreach (var ip in ReservedIps!)
            {
                await writer.EncodeAsync(
                        (ip ?? string.Empty).AsMemory(),
                        new EncodingContext(Encoding.UTF8, false),
                        LengthFormat.LittleEndian,
                        token)
                    .ConfigureAwait(false);
            }
        }
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

        var reservedIpsCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var reservedIps = new List<string>(reservedIpsCount);
        while (reservedIpsCount-- > 0)
        {
            using var ipOwner = await reader.DecodeAsync(
                new DecodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token: token).ConfigureAwait(false);
            reservedIps.Add(new string(ipOwner.Span));
        }

        return new ListRightPopCommand
        {
            Key = new string(keyOwner.Span),
            Count = count,
            NowTicks = nowTicks,
            IdTransaction = new string(txOwner.Span),
            ReservedIps = reservedIps
        };
    }
}

public struct ListRightPopCommandLegacy : ICommand<ListRightPopCommandLegacy>
{
    public const int Id = 4;
    static int ICommand<ListRightPopCommandLegacy>.Id => Id;

    public string Key { get; set; }
    public int Count { get; set; }
    public long NowTicks { get; set; }
    public string IdTransaction { get; set; }

    long? IDataTransferObject.Length => null;

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
    public static async ValueTask<ListRightPopCommandLegacy> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
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

        return new ListRightPopCommandLegacy
        {
            Key = new string(keyOwner.Span),
            Count = count,
            NowTicks = nowTicks,
            IdTransaction = new string(txOwner.Span)
        };
    }
}
