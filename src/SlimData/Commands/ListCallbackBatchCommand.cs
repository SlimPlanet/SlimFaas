using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListCallbackBatchCommand : ICommand<ListCallbackBatchCommand>
{
    public const int Id = 16; // Réserve un ID libre
    static int ICommand<ListCallbackBatchCommand>.Id => Id;

    public List<BatchItem> Items { get; set; }

    public struct BatchItem
    {
        public string Key { get; set; }
        public long NowTicks { get; set; }
        public List<CallbackElement> CallbackElements { get; set; }
    }

    long? IDataTransferObject.Length
    {
        get
        {
            long total = sizeof(int); // count Items
            if (Items is null) return total;

            foreach (var it in Items)
            {
                // Key: [int32 length][utf8 bytes]
                var key = it.Key ?? string.Empty;
                total += sizeof(int) + Encoding.UTF8.GetByteCount(key);

                // NowTicks
                total += sizeof(long);

                // count CallbackElements
                total += sizeof(int);

                if (it.CallbackElements is { Count: > 0 })
                {
                    foreach (var el in it.CallbackElements)
                    {
                        // Identifier: [int32 length][utf8 bytes]
                        var id = el.Identifier ?? string.Empty;
                        total += sizeof(int) + Encoding.UTF8.GetByteCount(id);

                        // HttpCode
                        total += sizeof(int);
                    }
                }
            }

            return total;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var count = Items?.Count ?? 0;
        await writer.WriteLittleEndianAsync(count, token).ConfigureAwait(false);
        if (count == 0) return;

        foreach (var it in Items!)
        {
            // Key
            await writer.EncodeAsync(it.Key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian, token).ConfigureAwait(false);

            // NowTicks
            await writer.WriteLittleEndianAsync(it.NowTicks, token).ConfigureAwait(false);

            // CallbackElements
            var elCount = it.CallbackElements?.Count ?? 0;
            await writer.WriteLittleEndianAsync(elCount, token).ConfigureAwait(false);

            if (elCount > 0)
            {
                foreach (var el in it.CallbackElements!)
                {
                    await writer.EncodeAsync(el.Identifier.AsMemory(),
                        new EncodingContext(Encoding.UTF8, false),
                        LengthFormat.LittleEndian, token).ConfigureAwait(false);

                    await writer.WriteLittleEndianAsync(el.HttpCode, token).ConfigureAwait(false);
                }
            }
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListCallbackBatchCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var count = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var items = new List<BatchItem>(count);

        for (int i = 0; i < count; i++)
        {
            // Key
            using var keyOwner = await reader.DecodeAsync(
                new DecodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            string key = new string(keyOwner.Span);

            // NowTicks
            var nowTicks = await reader.ReadLittleEndianAsync<Int64>(token).ConfigureAwait(false);

            // CallbackElements
            var elCount = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
            var elements = new List<CallbackElement>(elCount);

            while (elCount-- > 0)
            {
                using var idOwner = await reader.DecodeAsync(
                    new DecodingContext(Encoding.UTF8, false),
                    LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
                string identifier = new string(idOwner.Span);
                var httpCode = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
                elements.Add(new CallbackElement(identifier, httpCode));
            }

            items.Add(new BatchItem
            {
                Key = key,
                NowTicks = nowTicks,
                CallbackElements = elements
            });
        }

        return new ListCallbackBatchCommand { Items = items };
    }
}
