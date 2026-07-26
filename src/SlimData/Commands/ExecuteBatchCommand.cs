using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ExecuteBatchCommand : ICommand<ExecuteBatchCommand>
{
    public const int Id = 20;
    static int ICommand<ExecuteBatchCommand>.Id => Id;

    public ReadOnlyMemory<byte> Payload { get; set; }

    long? IDataTransferObject.Length
        => checked(SlimDataCommandCodec.HeaderLength + SlimDataCommandCodec.GetBytesLength(Payload.Length));

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(ExecuteBatchCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteBytesAsync(output, Payload, nameof(Payload), token).ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<ExecuteBatchCommand> ReadFromAsync<TReader>(
        TReader reader,
        CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(ExecuteBatchCommand), token)
                .ConfigureAwait(false);
            var payload = await SlimDataCommandCodec.ReadBytesAsync(input, nameof(Payload), token)
                .ConfigureAwait(false);
            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(ExecuteBatchCommand));
            return new ExecuteBatchCommand { Payload = payload };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex))
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(ExecuteBatchCommand), ex);
        }
    }
}
