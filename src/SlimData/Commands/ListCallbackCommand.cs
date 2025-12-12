using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public record CallbackElement(string Identifier, int HttpCode);

public struct ListCallbackCommand() : ICommand<ListCallbackCommand>
{
    public const int Id = 15;
    static int ICommand<ListCallbackCommand>.Id => Id;

    public string Key { get; set; }
    public long NowTicks { get; set; }
    public IList<CallbackElement> CallbackElements { get; set; }

    long? IDataTransferObject.Length
    {
        get
        {
            var key = Key ?? string.Empty;

            long result = 0;

            // Key: [int32 length][utf8 bytes]
            result += sizeof(int) + Encoding.UTF8.GetByteCount(key);

            // NowTicks
            result += sizeof(long);

            // CallbackElements.Count
            int count = CallbackElements?.Count ?? 0;
            result += sizeof(int);

            if (count > 0)
            {
                foreach (var element in CallbackElements!)
                {
                    var id = element?.Identifier ?? string.Empty;

                    // Identifier: [int32 length][utf8 bytes]
                    result += sizeof(int) + Encoding.UTF8.GetByteCount(id);

                    // HttpCode
                    result += sizeof(int);
                }
            }

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var key = Key ?? string.Empty;
        var elements = CallbackElements;

        await writer.EncodeAsync(
                key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);

        int count = elements?.Count ?? 0;
        await writer.WriteLittleEndianAsync(count, token).ConfigureAwait(false);

        if (count == 0)
            return;

        foreach (var element in elements!)
        {
            var id = element?.Identifier ?? string.Empty;

            await writer.EncodeAsync(
                    id.AsMemory(),
                    new EncodingContext(Encoding.UTF8, false),
                    LengthFormat.LittleEndian,
                    token)
                .ConfigureAwait(false);

            await writer.WriteLittleEndianAsync(element.HttpCode, token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListCallbackCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        var nowTicks = await reader.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);

        var callbackElementsCount = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
        var callbackElements = new List<CallbackElement>(callbackElementsCount);

        while (callbackElementsCount-- > 0)
        {
            using var idOwner = await reader.DecodeAsync(
                new DecodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token: token).ConfigureAwait(false);

            var httpCode = await reader.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);

            callbackElements.Add(new CallbackElement(new string(idOwner.Span), httpCode));
        }

        return new ListCallbackCommand
        {
            Key = new string(keyOwner.Span),
            NowTicks = nowTicks,
            CallbackElements = callbackElements
        };
    }
}
