using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListLeftPushCommand : ICommand<ListLeftPushCommand>
{
    public const int Id = 13;
    static int ICommand<ListLeftPushCommand>.Id => Id;

    public string Key { get; set; }
    public string Identifier { get; set; }
    public long NowTicks { get; set; }
    public int RetryTimeout { get; set; }
    public List<int> Retries { get; set; }
    public List<int> HttpStatusCodesWorthRetrying { get; set; }
    public ReadOnlyMemory<byte> Value { get; set; }

    long? IDataTransferObject.Length
    {
        get
        {
            var key = Key ?? string.Empty;
            var identifier = Identifier ?? string.Empty;

            long len = 0;

            // Key: [int32 length][utf8 bytes]
            len += sizeof(int) + Encoding.UTF8.GetByteCount(key);

            // Identifier: [int32 length][utf8 bytes]
            len += sizeof(int) + Encoding.UTF8.GetByteCount(identifier);

            // NowTicks (int64)
            len += sizeof(long);

            // RetryTimeout (int32)
            len += sizeof(int);

            // Value: [7-bit length][bytes] (LengthFormat.Compressed)
            int valueLen = Value.Length;
            len += Get7BitEncodedIntSize(valueLen) + valueLen;

            // Retries: [int32 count][int32...]
            int retriesCount = Retries?.Count ?? 0;
            len += sizeof(int) + (long)retriesCount * sizeof(int);

            // HttpStatusCodesWorthRetrying: [int32 count][int32...]
            int httpCount = HttpStatusCodesWorthRetrying?.Count ?? 0;
            len += sizeof(int) + (long)httpCount * sizeof(int);

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
        where TWriter : IAsyncBinaryWriter
    {
        var key = Key ?? string.Empty;
        var identifier = Identifier ?? string.Empty;
        var retries = Retries;
        var httpStatuses = HttpStatusCodesWorthRetrying;

        await writer.EncodeAsync(
                key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        await writer.EncodeAsync(
                identifier.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(RetryTimeout, token).ConfigureAwait(false);

        await writer.WriteAsync(Value, LengthFormat.Compressed, token).ConfigureAwait(false);

        int retriesCount = retries?.Count ?? 0;
        await writer.WriteLittleEndianAsync(retriesCount, token).ConfigureAwait(false);
        if (retriesCount > 0)
        {
            foreach (var r in retries!)
                await writer.WriteLittleEndianAsync(r, token).ConfigureAwait(false);
        }

        int httpCount = httpStatuses?.Count ?? 0;
        await writer.WriteLittleEndianAsync(httpCount, token).ConfigureAwait(false);
        if (httpCount > 0)
        {
            foreach (var h in httpStatuses!)
                await writer.WriteLittleEndianAsync(h, token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListLeftPushCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        using var idOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        var nowTicks = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);
        var timeout = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

        using var valueOwner = await reader.ReadAsync(
            LengthFormat.Compressed,
            token: token).ConfigureAwait(false);

        var retriesCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var retries = new List<int>(retriesCount);
        while (retriesCount-- > 0)
            retries.Add(await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false));

        var httpCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var httpStatuses = new List<int>(httpCount);
        while (httpCount-- > 0)
            httpStatuses.Add(await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false));

        return new ListLeftPushCommand
        {
            Key = new string(keyOwner.Span),
            Identifier = new string(idOwner.Span),
            NowTicks = nowTicks,
            RetryTimeout = timeout,
            Value = valueOwner.Memory.ToArray(),
            Retries = retries,
            HttpStatusCodesWorthRetrying = httpStatuses
        };
    }
}
