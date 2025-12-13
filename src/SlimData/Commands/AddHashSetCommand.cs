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

    long? IDataTransferObject.Length
    {
        get
        {
            var key = Key ?? string.Empty;

            long result = 0;

            // Key: [int32 length][utf8 bytes]
            result += sizeof(int) + Encoding.UTF8.GetByteCount(key);

            // hasTTL: byte
            result += sizeof(byte);

            // ExpireAtUtcTicks: int64 if present
            if (ExpireAtUtcTicks.HasValue)
                result += sizeof(long);

            // count: int32
            int count = Value?.Count ?? 0;
            result += sizeof(int);

            if (count > 0)
            {
                foreach (var kv in Value!)
                {
                    // entry key: [int32 length][utf8 bytes]
                    var entryKey = kv.Key ?? string.Empty;
                    result += sizeof(int) + Encoding.UTF8.GetByteCount(entryKey);

                    // entry value: [7-bit length][bytes] (LengthFormat.Compressed)
                    int valueLen = kv.Value.Length;
                    result += Get7BitEncodedIntSize(valueLen) + valueLen;
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
