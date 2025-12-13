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
    {
        get
        {
            var key = Key ?? string.Empty;

            // Key is encoded as: [int32 length][utf8 bytes] with LengthFormat.LittleEndian
            return sizeof(int) + Encoding.UTF8.GetByteCount(key);
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        await writer.EncodeAsync(
                Key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                token)
            .ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<DeleteKeyValueCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        return new DeleteKeyValueCommand
        {
            Key = new string(keyOwner.Span),
        };
    }
}