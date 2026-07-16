using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListLeftPushBatchCommand : ICommand<ListLeftPushBatchCommand>
{
    public const int Id = 14; // Choisis un ID libre
    static int ICommand<ListLeftPushBatchCommand>.Id => Id;

    public List<BatchItem> Items { get; set; }

    public struct BatchItem
    {
        public string Key { get; set; }
        public string Identifier { get; set; }
        public long NowTicks { get; set; }
        public int RetryTimeout { get; set; }
        public List<int> Retries { get; set; }
        public List<int> HttpStatusCodesWorthRetrying { get; set; }
        public ReadOnlyMemory<byte> Value { get; set; }
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
                    SlimDataCommandCodec.GetStringLength(item.Identifier) +
                    sizeof(long) +
                    sizeof(int) +
                    SlimDataCommandCodec.GetBytesLength(item.Value.Length) +
                    sizeof(int) +
                    ((item.Retries?.Count ?? 0) * sizeof(int)) +
                    sizeof(int) +
                    ((item.HttpStatusCodesWorthRetrying?.Count ?? 0) * sizeof(int)));
            }

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        var items = Items ?? [];
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(ListLeftPushBatchCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteCountAsync(
            output,
            items.Count,
            SlimDataCommandCodec.MaxBatchItems,
            nameof(Items),
            token).ConfigureAwait(false);

        foreach (var item in items)
        {
            await SlimDataCommandCodec.WriteStringAsync(output, item.Key, "Queue key", token).ConfigureAwait(false);
            await SlimDataCommandCodec.WriteStringAsync(output, item.Identifier, "Queue identifier", token)
                .ConfigureAwait(false);
            await output.WriteLittleEndianAsync(item.NowTicks, token).ConfigureAwait(false);
            await output.WriteLittleEndianAsync(item.RetryTimeout, token).ConfigureAwait(false);
            await SlimDataCommandCodec.WriteBytesAsync(output, item.Value, "Queue value", token)
                .ConfigureAwait(false);

            var retries = item.Retries ?? [];
            await SlimDataCommandCodec.WriteCountAsync(
                output,
                retries.Count,
                SlimDataCommandCodec.MaxCollectionCount,
                nameof(BatchItem.Retries),
                token).ConfigureAwait(false);
            foreach (var retry in retries)
                await output.WriteLittleEndianAsync(retry, token).ConfigureAwait(false);

            var statusCodes = item.HttpStatusCodesWorthRetrying ?? [];
            await SlimDataCommandCodec.WriteCountAsync(
                output,
                statusCodes.Count,
                SlimDataCommandCodec.MaxCollectionCount,
                nameof(BatchItem.HttpStatusCodesWorthRetrying),
                token).ConfigureAwait(false);
            foreach (var statusCode in statusCodes)
                await output.WriteLittleEndianAsync(statusCode, token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListLeftPushBatchCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            var legacyCount = await SlimDataCommandCodec.ReadHeaderOrLegacyCountAsync(
                    input,
                    nameof(ListLeftPushBatchCommand),
                    SlimDataCommandCodec.MaxBatchItems,
                    token)
                .ConfigureAwait(false);
            var count = legacyCount ?? await SlimDataCommandCodec.ReadCountAsync(
                    input,
                    SlimDataCommandCodec.MaxBatchItems,
                    nameof(Items),
                    token)
                .ConfigureAwait(false);
            var items = new List<BatchItem>(count);
            for (var i = 0; i < count; i++)
            {
                var key = await SlimDataCommandCodec.ReadStringAsync(input, "Queue key", token)
                    .ConfigureAwait(false);
                var identifier = await SlimDataCommandCodec.ReadStringAsync(input, "Queue identifier", token)
                    .ConfigureAwait(false);
                var nowTicks = await input.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);
                var retryTimeout = await input.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                var value = await SlimDataCommandCodec.ReadBytesAsync(input, "Queue value", token)
                    .ConfigureAwait(false);

                var retriesCount = await SlimDataCommandCodec.ReadCountAsync(
                    input,
                    SlimDataCommandCodec.MaxCollectionCount,
                    nameof(BatchItem.Retries),
                    token).ConfigureAwait(false);
                var retries = new List<int>(retriesCount);
                for (var retryIndex = 0; retryIndex < retriesCount; retryIndex++)
                    retries.Add(await input.ReadLittleEndianAsync<int>(token).ConfigureAwait(false));

                var statusCount = await SlimDataCommandCodec.ReadCountAsync(
                    input,
                    SlimDataCommandCodec.MaxCollectionCount,
                    nameof(BatchItem.HttpStatusCodesWorthRetrying),
                    token).ConfigureAwait(false);
                var statusCodes = new List<int>(statusCount);
                for (var statusIndex = 0; statusIndex < statusCount; statusIndex++)
                    statusCodes.Add(await input.ReadLittleEndianAsync<int>(token).ConfigureAwait(false));

                items.Add(new BatchItem
                {
                    Key = key,
                    Identifier = identifier,
                    NowTicks = nowTicks,
                    RetryTimeout = retryTimeout,
                    Value = value,
                    Retries = retries,
                    HttpStatusCodesWorthRetrying = statusCodes
                });
            }

            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(ListLeftPushBatchCommand));
            return new ListLeftPushBatchCommand { Items = items };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex))
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(ListLeftPushBatchCommand), ex);
        }
    }
}
