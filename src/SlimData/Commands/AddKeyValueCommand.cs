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

    long? IDataTransferObject.Length
    {
        get
        {
            var payloadLength = GetItemsPayloadLength(EffectiveItems());
            return checked(
                SlimDataCommandCodec.HeaderLength +
                sizeof(byte) +
                SlimDataCommandCodec.GetBytesLength(checked((int)payloadLength)));
        }
    }

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
        len += SlimDataCommandCodec.GetStringLength(key);
        len += sizeof(long); // now ticks
        len += sizeof(byte); // has ttl
        if (item.ExpireAtUtcTicks.HasValue)
            len += sizeof(long);
        len += sizeof(long); // integer delta
        len += sizeof(int) * 4; // decimal bits
        len += SlimDataCommandCodec.GetBytesLength(valueLen);
        return len;
    }

    private static long GetItemsPayloadLength(List<BatchItem> items)
    {
        long len = sizeof(int); // item count
        foreach (var item in items)
            len += GetItemLength(item);

        return len;
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        var items = EffectiveItems();
        var payloadLength = GetItemsPayloadLength(items);
        SlimDataCommandCodec.ValidateValueLength(payloadLength, "Key/value payload");
        SlimDataCommandCodec.ValidateCommandLength(
            checked(
                SlimDataCommandCodec.HeaderLength +
                sizeof(byte) +
                SlimDataCommandCodec.GetBytesLength(checked((int)payloadLength))),
            nameof(AddKeyValueCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await output.WriteLittleEndianAsync(SerializationVersion, token).ConfigureAwait(false);
        var payload = await SerializeItemsAsync(items, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteBytesAsync(output, payload, "Key/value payload", token)
            .ConfigureAwait(false);
    }

    private static async ValueTask<byte[]> SerializeItemsAsync(List<BatchItem> items, CancellationToken token)
    {
        var payloadLength = GetItemsPayloadLength(items);
        SlimDataCommandCodec.ValidateValueLength(payloadLength, "Key/value payload");
        await using var stream = new MemoryStream(checked((int)payloadLength));
        var writer = IAsyncBinaryWriter.Create(stream, new byte[8192]);
        await SlimDataCommandCodec.WriteCountAsync(
            writer,
            items.Count,
            SlimDataCommandCodec.MaxBatchItems,
            nameof(Items),
            token).ConfigureAwait(false);
        foreach (var item in items)
            await WriteItemAsync(writer, item, token).ConfigureAwait(false);

        return stream.ToArray();
    }

    private static async ValueTask WriteItemAsync<TWriter>(TWriter writer, BatchItem item, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        await writer.WriteLittleEndianAsync((byte)item.Operation, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(writer, item.Key, "Key/value key", token)
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

        await SlimDataCommandCodec.WriteBytesAsync(writer, item.Value, "Key/value value", token)
            .ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<AddKeyValueCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(AddKeyValueCommand), token)
                .ConfigureAwait(false);
            var version = await input.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
            if (version != SerializationVersion)
            {
                throw new SlimDataCommandFormatException(
                    SlimDataCommandViolation.UnsupportedVersion,
                    $"Unsupported AddKeyValueCommand version {version}; expected {SerializationVersion}.");
            }

            var command = await ReadVersion3Async(input, token).ConfigureAwait(false);
            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(AddKeyValueCommand));
            return command;
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex) || ex is InvalidDataException)
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(AddKeyValueCommand), ex);
        }
    }

#pragma warning disable CA2252
    private static async ValueTask<AddKeyValueCommand> ReadVersion3Async<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var payload = await SlimDataCommandCodec.ReadBytesAsync(reader, "Key/value payload", token)
            .ConfigureAwait(false);
        using var stream = new MemoryStream(payload, writable: false);
        var payloadReader = IAsyncBinaryReader.Create(stream, new byte[8192]);

        var count = await SlimDataCommandCodec.ReadCountAsync(
            payloadReader,
            SlimDataCommandCodec.MaxBatchItems,
            nameof(Items),
            token).ConfigureAwait(false);

        var items = new List<BatchItem>(count);
        for (var i = 0; i < count; i++)
            items.Add(await ReadItemAsync(payloadReader, token).ConfigureAwait(false));

        SlimDataCommandCodec.EnsureFullyConsumed(payloadReader, "Key/value payload");
        return ToCommand(items);
    }

    private static AddKeyValueCommand ToCommand(BatchItem item)
        => ToCommand(new List<BatchItem>(1) { item });

    private static AddKeyValueCommand ToCommand(List<BatchItem> items)
    {
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
        return await ReadItemAfterOperationAsync(reader, operation, token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    private static async ValueTask<BatchItem> ReadItemAfterOperationAsync<TReader>(
        TReader reader,
        KeyValueOperation operation,
        CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        if (!Enum.IsDefined(operation))
            throw new InvalidDataException($"Unsupported AddKeyValueCommand operation byte {(byte)operation}.");

        var key = await SlimDataCommandCodec.ReadStringAsync(reader, "Key/value key", token)
            .ConfigureAwait(false);

        var nowTicks = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var hasTtl = await reader.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
        long? expire = null;
        if (hasTtl > 1)
            throw new InvalidDataException("Invalid key/value TTL marker.");
        if (hasTtl == 1)
            expire = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var integerDelta = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var decimalBits = new int[4];
        for (var i = 0; i < decimalBits.Length; i++)
            decimalBits[i] = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var floatDelta = new decimal(decimalBits);

        var value = await SlimDataCommandCodec.ReadBytesAsync(reader, "Key/value value", token)
            .ConfigureAwait(false);

        return new BatchItem
        {
            Operation = operation,
            Key = key,
            Value = value,
            IntegerDelta = integerDelta,
            FloatDelta = floatDelta,
            ExpireAtUtcTicks = expire,
            NowTicks = nowTicks
        };
    }
}
