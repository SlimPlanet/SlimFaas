using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct AddKeyValueCommand : ICommand<AddKeyValueCommand>
{
    private const byte SerializationVersion = 3;
    public const int Id = 2;
    static int ICommand<AddKeyValueCommand>.Id => Id;

    public struct BatchItem
    {
        public KeyValueOperation Operation { get; set; }
        public string Key { get; set; }
        public ReadOnlyMemory<byte> Value { get; set; }
        public long IntegerDelta { get; set; }
        public decimal FloatDelta { get; set; }
        public long? ExpireAtUtcTicks { get; set; }
        public long NowTicks { get; set; }
    }

    public List<BatchItem>? Items { get; set; }
    public KeyValueOperation Operation { get; set; }
    public string Key { get; set; }
    public ReadOnlyMemory<byte> Value { get; set; }
    public long IntegerDelta { get; set; }
    public decimal FloatDelta { get; set; }
    public long? ExpireAtUtcTicks { get; set; }
    public long NowTicks { get; set; }

    long? IDataTransferObject.Length => null;

    public List<BatchItem> EffectiveItems()
    {
        if (Items is { Count: > 0 })
            return Items;

        return new List<BatchItem>(1)
        {
            new()
            {
                Operation = Operation,
                Key = Key,
                Value = Value,
                IntegerDelta = IntegerDelta,
                FloatDelta = FloatDelta,
                ExpireAtUtcTicks = ExpireAtUtcTicks,
                NowTicks = NowTicks
            }
        };
    }

    private static long GetItemLength(BatchItem item)
    {
        var key = item.Key ?? string.Empty;
        var valueLen = item.Value.Length;

        long len = 0;
        len += sizeof(byte); // operation
        len += sizeof(int) + Encoding.UTF8.GetByteCount(key);
        len += sizeof(long); // now ticks
        len += sizeof(byte); // has ttl
        if (item.ExpireAtUtcTicks.HasValue)
            len += sizeof(long);
        len += sizeof(long); // integer delta
        len += sizeof(int) * 4; // decimal bits
        len += Get7BitEncodedIntSize(valueLen) + valueLen;
        return len;
    }

    private static long GetItemsPayloadLength(List<BatchItem> items)
    {
        long len = sizeof(int); // item count
        foreach (var item in items)
            len += GetItemLength(item);

        return len;
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
        var items = EffectiveItems();
        await writer.WriteLittleEndianAsync(SerializationVersion, token).ConfigureAwait(false);
        var payload = await SerializeItemsAsync(items, token).ConfigureAwait(false);
        await writer.WriteAsync(payload, LengthFormat.Compressed, token).ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> SerializeItemsAsync(List<BatchItem> items, CancellationToken token)
    {
        await using var stream = new MemoryStream((int)GetItemsPayloadLength(items));
        var writer = IAsyncBinaryWriter.Create(stream, new byte[8192]);
        await writer.WriteLittleEndianAsync(items.Count, token).ConfigureAwait(false);
        foreach (var item in items)
            await WriteItemAsync(writer, item, token).ConfigureAwait(false);

        return stream.ToArray();
    }

    private static async ValueTask WriteItemAsync<TWriter>(TWriter writer, BatchItem item, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var key = item.Key ?? string.Empty;

        await writer.WriteLittleEndianAsync((byte)item.Operation, token).ConfigureAwait(false);
        await writer.EncodeAsync(
                key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(item.NowTicks, token).ConfigureAwait(false);

        byte hasTtl = (byte)(item.ExpireAtUtcTicks.HasValue ? 1 : 0);
        await writer.WriteLittleEndianAsync(hasTtl, token).ConfigureAwait(false);
        if (item.ExpireAtUtcTicks.HasValue)
            await writer.WriteLittleEndianAsync(item.ExpireAtUtcTicks.Value, token).ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(item.IntegerDelta, token).ConfigureAwait(false);

        var decimalBits = decimal.GetBits(item.FloatDelta);
        for (var i = 0; i < decimalBits.Length; i++)
            await writer.WriteLittleEndianAsync(decimalBits[i], token).ConfigureAwait(false);

        await writer.WriteAsync(item.Value, LengthFormat.Compressed, token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<AddKeyValueCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var version = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        if (version != SerializationVersion)
            throw new InvalidDataException($"Unsupported AddKeyValueCommand version {version}.");

        using var payloadOwner = await reader.ReadAsync(
            LengthFormat.Compressed,
            token: token).ConfigureAwait(false);
        using var stream = new MemoryStream(payloadOwner.Memory.ToArray());
        var payloadReader = IAsyncBinaryReader.Create(stream, new byte[8192]);

        var count = await payloadReader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        if (count < 0)
            throw new InvalidDataException("Invalid AddKeyValueCommand item count.");

        var items = new List<BatchItem>(count);
        for (var i = 0; i < count; i++)
            items.Add(await ReadItemAsync(payloadReader, token).ConfigureAwait(false));

        var command = new AddKeyValueCommand { Items = items };
        if (items.Count > 0)
        {
            var first = items[0];
            command.Operation = first.Operation;
            command.Key = first.Key;
            command.Value = first.Value;
            command.IntegerDelta = first.IntegerDelta;
            command.FloatDelta = first.FloatDelta;
            command.ExpireAtUtcTicks = first.ExpireAtUtcTicks;
            command.NowTicks = first.NowTicks;
        }

        return command;
    }

#pragma warning disable CA2252
    private static async ValueTask<BatchItem> ReadItemAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
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

        return new BatchItem
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
