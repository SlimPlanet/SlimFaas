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

    public string Key { get; set; } = string.Empty;
    public long NowTicks { get; set; }
    public IList<CallbackElement> CallbackElements { get; set; } = [];

    long? IDataTransferObject.Length
    {
        get
        {
            var result = checked(
                SlimDataCommandCodec.HeaderLength +
                SlimDataCommandCodec.GetStringLength(Key) +
                sizeof(long) +
                sizeof(int));
            foreach (var element in CallbackElements ?? [])
            {
                result = checked(
                    result +
                    SlimDataCommandCodec.GetStringLength(element?.Identifier) +
                    sizeof(int));
            }

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        var elements = CallbackElements ?? [];
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(ListCallbackCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(output, Key, nameof(Key), token).ConfigureAwait(false);

        await output.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);

        await SlimDataCommandCodec.WriteCountAsync(
            output,
            elements.Count,
            SlimDataCommandCodec.MaxCollectionCount,
            nameof(CallbackElements),
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

#pragma warning disable CA2252
    public static async ValueTask<ListCallbackCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(ListCallbackCommand), token)
                .ConfigureAwait(false);
            var key = await SlimDataCommandCodec.ReadStringAsync(input, nameof(Key), token).ConfigureAwait(false);
            var nowTicks = await input.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);
            var callbackElementsCount = await SlimDataCommandCodec.ReadCountAsync(
                input,
                SlimDataCommandCodec.MaxCollectionCount,
                nameof(CallbackElements),
                token).ConfigureAwait(false);
            var callbackElements = new List<CallbackElement>(callbackElementsCount);
            for (var i = 0; i < callbackElementsCount; i++)
            {
                var identifier = await SlimDataCommandCodec.ReadStringAsync(
                    input,
                    "Callback identifier",
                    token).ConfigureAwait(false);
                var httpCode = await input.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
                callbackElements.Add(new CallbackElement(identifier, httpCode));
            }

            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(ListCallbackCommand));
            return new ListCallbackCommand
            {
                Key = key,
                NowTicks = nowTicks,
                CallbackElements = callbackElements
            };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex))
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(ListCallbackCommand), ex);
        }
    }
}
