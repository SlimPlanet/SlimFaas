using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct DeleteKeyValueCommand : ICommand<DeleteKeyValueCommand>
{
    public const int Id = 3;
    static int ICommand<DeleteKeyValueCommand>.Id => Id;

    public string Key { get; set; }

    long? IDataTransferObject.Length
        => checked(SlimDataCommandCodec.HeaderLength + SlimDataCommandCodec.GetStringLength(Key));

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(DeleteKeyValueCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(output, Key, nameof(Key), token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<DeleteKeyValueCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(DeleteKeyValueCommand), token)
                .ConfigureAwait(false);
            var key = await SlimDataCommandCodec.ReadStringAsync(input, nameof(Key), token).ConfigureAwait(false);
            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(DeleteKeyValueCommand));
            return new DeleteKeyValueCommand { Key = key };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex))
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(DeleteKeyValueCommand), ex);
        }
    }
}
