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

    // Estimation de longueur, sur le même principe que la commande simple :
    // (on ne compte pas les préfixes de longueur ajoutés par EncodeAsync, exactement comme l'implémentation single).
    long? IDataTransferObject.Length
    {
        get
        {
            if (Items is null) return sizeof(int);
            long total = sizeof(int); // nombre d'items
            foreach (var it in Items)
            {
                total += Encoding.UTF8.GetByteCount(it.Key);
                total += Encoding.UTF8.GetByteCount(it.Identifier);
                total += sizeof(long); // NowTicks
                total += sizeof(int);  // RetryTimeout
                total += it.Value.Length; // payload compressé (on compte la donnée)
                total += sizeof(int) + (long)(it.Retries?.Count ?? 0) * sizeof(int); // count + items
                total += sizeof(int) + (long)(it.HttpStatusCodesWorthRetrying?.Count ?? 0) * sizeof(int);
            }
            return total;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : IAsyncBinaryWriter
    {
        var count = Items?.Count ?? 0;
        await writer.WriteLittleEndianAsync(count, token).ConfigureAwait(false);

        if (count == 0)
            return;

        foreach (var it in Items!)
        {
            // Strings
            await writer.EncodeAsync(it.Key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian, token).ConfigureAwait(false);

            await writer.EncodeAsync(it.Identifier.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian, token).ConfigureAwait(false);

            // Champs scalaires
            await writer.WriteLittleEndianAsync(it.NowTicks, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(it.RetryTimeout, token).ConfigureAwait(false);

            // Payload
            await writer.WriteAsync(it.Value, LengthFormat.Compressed, token).ConfigureAwait(false);

            // Retries
            var retriesCount = it.Retries?.Count ?? 0;
            await writer.WriteLittleEndianAsync(retriesCount, token).ConfigureAwait(false);
            if (retriesCount > 0)
            {
                foreach (var r in it.Retries!)
                    await writer.WriteLittleEndianAsync(r, token).ConfigureAwait(false);
            }

            // HttpStatusCodesWorthRetrying
            var httpCount = it.HttpStatusCodesWorthRetrying?.Count ?? 0;
            await writer.WriteLittleEndianAsync(httpCount, token).ConfigureAwait(false);
            if (httpCount > 0)
            {
                foreach (var h in it.HttpStatusCodesWorthRetrying!)
                    await writer.WriteLittleEndianAsync(h, token).ConfigureAwait(false);
            }
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListLeftPushBatchCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var count = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var items = new List<BatchItem>(count);

        for (int i = 0; i < count; i++)
        {
            // Strings
            var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false),
                                               LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            var identifier = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false),
                                                      LengthFormat.LittleEndian, token: token).ConfigureAwait(false);

            // Scalaires
            var nowTicks = await reader.ReadLittleEndianAsync<Int64>(token).ConfigureAwait(false);
            var retryTimeout = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);

            // Payload
            using var valueOwner = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
            var value = valueOwner.Memory.ToArray();

            // Retries
            var retriesCount = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
            var retries = new List<int>(retriesCount);
            while (retriesCount-- > 0)
                retries.Add(await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false));

            // HttpStatusCodesWorthRetrying
            var httpCount = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
            var httpStatuses = new List<int>(httpCount);
            while (httpCount-- > 0)
                httpStatuses.Add(await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false));

            items.Add(new BatchItem
            {
                Key = key.ToString(),
                Identifier = identifier.ToString(),
                NowTicks = nowTicks,
                RetryTimeout = retryTimeout,
                Value = value,
                Retries = retries,
                HttpStatusCodesWorthRetrying = httpStatuses
            });
        }

        return new ListLeftPushBatchCommand { Items = items };
    }
}
