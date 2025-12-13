using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct AddKeyValueCommand : ICommand<AddKeyValueCommand>
{
    public const int Id = 2;
    static int ICommand<AddKeyValueCommand>.Id => Id;

    public string Key { get; set; }
    public ReadOnlyMemory<byte> Value { get; set; }
    public long? ExpireAtUtcTicks { get; set; }

    long? IDataTransferObject.Length
    {
        get
        {
            var key = Key ?? string.Empty;

            long len = 0;

            // Key: [int32 length][utf8 bytes]
            len += sizeof(int) + Encoding.UTF8.GetByteCount(key);

            // hasTTL: byte
            len += sizeof(byte);

            // ExpireAtUtcTicks: int64 if present
            if (ExpireAtUtcTicks.HasValue)
                len += sizeof(long);

            // Value: [7-bit length][bytes] (LengthFormat.Compressed)
            int valueLen = Value.Length;
            len += Get7BitEncodedIntSize(valueLen) + valueLen;

            return len;
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
        // EncodeAsync n'accepte pas null => on force string.Empty
        var key = Key ?? string.Empty;

        await writer.EncodeAsync(
                key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        byte hasTtl = (byte)(ExpireAtUtcTicks.HasValue ? 1 : 0);
        await writer.WriteLittleEndianAsync(hasTtl, token).ConfigureAwait(false);

        if (ExpireAtUtcTicks.HasValue)
            await writer.WriteLittleEndianAsync(ExpireAtUtcTicks.Value, token).ConfigureAwait(false);

        await writer.WriteAsync(Value, LengthFormat.Compressed, token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<AddKeyValueCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        // DecodeAsync => owner (buffer loué) => Span<char>
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        var hasTtl = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        long? expire = null;
        if (hasTtl != 0)
            expire = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        // ReadAsync => owner (buffer loué) => on doit copier avant Dispose()
        using var valueOwner = await reader.ReadAsync(
            LengthFormat.Compressed,
            token: token).ConfigureAwait(false);

        return new AddKeyValueCommand
        {
            Key = new string(keyOwner.Span),
            Value = valueOwner.Memory.ToArray(),
            ExpireAtUtcTicks = expire
        };
    }
}
