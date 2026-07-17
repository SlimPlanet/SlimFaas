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
            long result = checked(SlimDataCommandCodec.HeaderLength + sizeof(int));
            foreach (var item in Items ?? [])
            {
                result = checked(
                    result +
                    SlimDataCommandCodec.GetStringLength(item.Key) +
                    sizeof(long) +
                    sizeof(int));
                foreach (var element in item.CallbackElements ?? [])
                {
                    result = checked(
                        result +
                        SlimDataCommandCodec.GetStringLength(element?.Identifier) +
                        sizeof(int));
                }
            }

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        var items = Items ?? [];
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(ListCallbackBatchCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteCountAsync(
            output,
            items.Count,
            SlimDataCommandCodec.MaxBatchItems,
            nameof(Items),
            token).ConfigureAwait(false);

        foreach (var item in items)
        {
            await SlimDataCommandCodec.WriteStringAsync(output, item.Key, "Callback batch key", token)
                .ConfigureAwait(false);
            await output.WriteLittleEndianAsync(item.NowTicks, token).ConfigureAwait(false);
            var elements = item.CallbackElements ?? [];
            await SlimDataCommandCodec.WriteCountAsync(
                output,
                elements.Count,
                SlimDataCommandCodec.MaxCollectionCount,
                nameof(BatchItem.CallbackElements),
                token).ConfigureAwait(false);
            foreach (var element in elements)
            {
                await SlimDataCommandCodec.WriteStringAsync(
                    output,
                    element?.Identifier,
                    "Callback identifier",
                    token).ConfigureAwait(false);
                await output.WriteLittleEndianAsync(element?.HttpCode ?? 0, token).ConfigureAwait(false);
            }
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListCallbackBatchCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(ListCallbackBatchCommand), token)
                .ConfigureAwait(false);
            var count = await SlimDataCommandCodec.ReadCountAsync(
                input,
                SlimDataCommandCodec.MaxBatchItems,
                nameof(Items),
                token).ConfigureAwait(false);
            var items = new List<BatchItem>(count);
            for (var i = 0; i < count; i++)
            {
                var key = await SlimDataCommandCodec.ReadStringAsync(input, "Callback batch key", token)
                    .ConfigureAwait(false);
                var nowTicks = await input.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);
                var elementCount = await SlimDataCommandCodec.ReadCountAsync(
                    input,
                    SlimDataCommandCodec.MaxCollectionCount,
                    nameof(BatchItem.CallbackElements),
                    token).ConfigureAwait(false);
                var elements = new List<CallbackElement>(elementCount);
                for (var elementIndex = 0; elementIndex < elementCount; elementIndex++)
                {
                    var identifier = await SlimDataCommandCodec.ReadStringAsync(
                        input,
                        "Callback identifier",
                        token).ConfigureAwait(false);
                    var httpCode = await input.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                    elements.Add(new CallbackElement(identifier, httpCode));
                }

                items.Add(new BatchItem
                {
                    Key = key,
                    NowTicks = nowTicks,
                    CallbackElements = elements
                });
            }

            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(ListCallbackBatchCommand));
            return new ListCallbackBatchCommand { Items = items };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex))
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(ListCallbackBatchCommand), ex);
        }
    }
}
