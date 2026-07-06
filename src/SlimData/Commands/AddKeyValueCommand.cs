using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct AddKeyValueCommand : ICommand<AddKeyValueCommand>
{
    private const byte SerializationVersion = 2;
    public const int Id = 2;
    static int ICommand<AddKeyValueCommand>.Id => Id;

    public KeyValueOperation Operation { get; set; }
    public string Key { get; set; }
    public ReadOnlyMemory<byte> Value { get; set; }
    public long IntegerDelta { get; set; }
    public decimal FloatDelta { get; set; }
    public long? ExpireAtUtcTicks { get; set; }
    public long NowTicks { get; set; }

    long? IDataTransferObject.Length
    {
        get
        {
            var key = Key ?? string.Empty;
            var valueLen = Value.Length;

            long len = 0;
            len += sizeof(byte); // version
            len += sizeof(byte); // operation
            len += sizeof(int) + Encoding.UTF8.GetByteCount(key);
            len += sizeof(long); // now ticks
            len += sizeof(byte); // has ttl
            if (ExpireAtUtcTicks.HasValue)
                len += sizeof(long);
            len += sizeof(long); // integer delta
            len += sizeof(int) * 4; // decimal bits
            len += Get7BitEncodedIntSize(valueLen) + valueLen;
            return len;
        }
    }

    private static int Get7BitEncodedIntSize(int value)
    {
        uint v = (uint)value;
        var size = 1;
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
        var key = Key ?? string.Empty;

        await writer.WriteLittleEndianAsync(SerializationVersion, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync((byte)Operation, token).ConfigureAwait(false);
        await writer.EncodeAsync(
                key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);

        byte hasTtl = (byte)(ExpireAtUtcTicks.HasValue ? 1 : 0);
        await writer.WriteLittleEndianAsync(hasTtl, token).ConfigureAwait(false);
        if (ExpireAtUtcTicks.HasValue)
            await writer.WriteLittleEndianAsync(ExpireAtUtcTicks.Value, token).ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(IntegerDelta, token).ConfigureAwait(false);

        var decimalBits = decimal.GetBits(FloatDelta);
        for (var i = 0; i < decimalBits.Length; i++)
            await writer.WriteLittleEndianAsync(decimalBits[i], token).ConfigureAwait(false);

        await writer.WriteAsync(Value, LengthFormat.Compressed, token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<AddKeyValueCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var version = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        if (version != SerializationVersion)
            throw new InvalidDataException($"Unsupported AddKeyValueCommand version {version}.");

        var operation = (KeyValueOperation)await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);

        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        var nowTicks = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var hasTtl = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        long? expire = null;
        if (hasTtl != 0)
            expire = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var integerDelta = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var decimalBits = new int[4];
        for (var i = 0; i < decimalBits.Length; i++)
            decimalBits[i] = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var floatDelta = new decimal(decimalBits);

        using var valueOwner = await reader.ReadAsync(
            LengthFormat.Compressed,
            token: token).ConfigureAwait(false);

        return new AddKeyValueCommand
        {
            Operation = operation,
            Key = new string(keyOwner.Span),
            Value = valueOwner.Memory.ToArray(),
            IntegerDelta = integerDelta,
            FloatDelta = floatDelta,
            ExpireAtUtcTicks = expire,
            NowTicks = nowTicks
        };
    }
}
