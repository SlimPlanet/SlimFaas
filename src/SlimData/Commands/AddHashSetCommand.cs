using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct AddHashSetCommand : ICommand<AddHashSetCommand>
{
    public const int Id = 1;
    static int ICommand<AddHashSetCommand>.Id => Id;

    public string Key { get; set; }
    public Dictionary<string, ReadOnlyMemory<byte>> Value { get; set; }

    public long? ExpireAtUtcTicks { get; set; }

    long? IDataTransferObject.Length => null;

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var ctx = new EncodingContext(Encoding.UTF8, true);

        await writer.EncodeAsync(Key.AsMemory(), ctx, LengthFormat.LittleEndian, token).ConfigureAwait(false);

        byte hasTtl = (byte)(ExpireAtUtcTicks.HasValue ? 1 : 0);
        await writer.WriteLittleEndianAsync(hasTtl, token).ConfigureAwait(false);

        if (ExpireAtUtcTicks.HasValue)
            await writer.WriteLittleEndianAsync(ExpireAtUtcTicks.Value, token).ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(Value.Count, token).ConfigureAwait(false);

        foreach (var (k, v) in Value)
        {
            await writer.EncodeAsync(k.AsMemory(), ctx, LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteAsync(v, LengthFormat.Compressed, token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<AddHashSetCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        var hasTtl = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        long? expire = null;
        if (hasTtl != 0)
            expire = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var count = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

        var dict = new Dictionary<string, ReadOnlyMemory<byte>>(count);
        var ctx = new DecodingContext(Encoding.UTF8, true);

        while (count-- > 0)
        {
            using var entryKeyOwner = await reader.DecodeAsync(
                ctx,
                LengthFormat.LittleEndian,
                token: token).ConfigureAwait(false);

            using var valueOwner = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);

            dict.Add(new string(entryKeyOwner.Span), valueOwner.Memory.ToArray());
        }

        return new AddHashSetCommand
        {
            Key = new string(keyOwner.Span),
            Value = dict,
            ExpireAtUtcTicks = expire
        };
    }
}
