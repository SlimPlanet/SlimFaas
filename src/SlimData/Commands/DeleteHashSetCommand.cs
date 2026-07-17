using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct DeleteHashSetCommand : ICommand<DeleteHashSetCommand>
{
    public const int Id = 17;
    static int ICommand<DeleteHashSetCommand>.Id => Id;

    public string Key { get; set; }
    public string DictionaryKey { get; set; }

    long? IDataTransferObject.Length
        => checked(
            SlimDataCommandCodec.HeaderLength +
            SlimDataCommandCodec.GetStringLength(Key) +
            SlimDataCommandCodec.GetStringLength(DictionaryKey));

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(DeleteHashSetCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(output, Key, nameof(Key), token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(output, DictionaryKey, nameof(DictionaryKey), token)
            .ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<DeleteHashSetCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(DeleteHashSetCommand), token)
                .ConfigureAwait(false);
            var key = await SlimDataCommandCodec.ReadStringAsync(input, nameof(Key), token).ConfigureAwait(false);
            var dictionaryKey = await SlimDataCommandCodec.ReadStringAsync(input, nameof(DictionaryKey), token)
                .ConfigureAwait(false);
            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(DeleteHashSetCommand));
            return new DeleteHashSetCommand { Key = key, DictionaryKey = dictionaryKey };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex))
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(DeleteHashSetCommand), ex);
        }
    }
}
