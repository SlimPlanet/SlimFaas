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
            var result = checked(
                SlimDataCommandCodec.HeaderLength +
                SlimDataCommandCodec.GetStringLength(Key) +
                sizeof(byte) +
                (ExpireAtUtcTicks.HasValue ? sizeof(long) : 0) +
                sizeof(int));
            foreach (var (key, value) in Value ?? [])
            {
                result = checked(
                    result +
                    SlimDataCommandCodec.GetStringLength(key) +
                    SlimDataCommandCodec.GetBytesLength(value.Length));
            }

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(AddHashSetCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(output, Key, nameof(Key), token).ConfigureAwait(false);

        byte hasTtl = (byte)(ExpireAtUtcTicks.HasValue ? 1 : 0);
        await output.WriteLittleEndianAsync(hasTtl, token).ConfigureAwait(false);

        if (ExpireAtUtcTicks.HasValue)
            await output.WriteLittleEndianAsync(ExpireAtUtcTicks.Value, token).ConfigureAwait(false);

        var values = Value ?? [];
        await SlimDataCommandCodec.WriteCountAsync(
            output,
            values.Count,
            SlimDataCommandCodec.MaxCollectionCount,
            nameof(Value),
            token).ConfigureAwait(false);

        foreach (var (k, v) in values)
        {
            await SlimDataCommandCodec.WriteStringAsync(output, k, "HashSet key", token).ConfigureAwait(false);
            await SlimDataCommandCodec.WriteBytesAsync(output, v, "HashSet value", token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<AddHashSetCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(AddHashSetCommand), token)
                .ConfigureAwait(false);
            var key = await SlimDataCommandCodec.ReadStringAsync(input, nameof(Key), token).ConfigureAwait(false);
            var hasTtl = await input.ReadLittleEndianAsync<byte>(token).ConfigureAwait(false);
            if (hasTtl > 1)
                throw new InvalidDataException("Invalid hashset TTL marker.");
            long? expire = hasTtl == 1
                ? await input.ReadLittleEndianAsync<long>(token).ConfigureAwait(false)
                : null;

            var count = await SlimDataCommandCodec.ReadCountAsync(
                input,
                SlimDataCommandCodec.MaxCollectionCount,
                nameof(Value),
                token).ConfigureAwait(false);
            var dictionary = new Dictionary<string, ReadOnlyMemory<byte>>(count);
            for (var i = 0; i < count; i++)
            {
                var dictionaryKey = await SlimDataCommandCodec.ReadStringAsync(input, "HashSet key", token)
                    .ConfigureAwait(false);
                var value = await SlimDataCommandCodec.ReadBytesAsync(input, "HashSet value", token)
                    .ConfigureAwait(false);
                dictionary.Add(dictionaryKey, value);
            }

            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(AddHashSetCommand));
            return new AddHashSetCommand { Key = key, Value = dictionary, ExpireAtUtcTicks = expire };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex) || ex is InvalidDataException)
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(AddHashSetCommand), ex);
        }
    }
}
