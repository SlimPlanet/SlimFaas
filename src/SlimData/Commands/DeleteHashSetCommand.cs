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
    {
        get
        {
            var key = Key ?? string.Empty;
            var dictKey = DictionaryKey ?? string.Empty;

            // Each string is encoded as: [int32 length][utf8 bytes] with LengthFormat.LittleEndian
            long result = 0;
            result += sizeof(int) + Encoding.UTF8.GetByteCount(key);
            result += sizeof(int) + Encoding.UTF8.GetByteCount(dictKey);

            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        var context = new EncodingContext(Encoding.UTF8, true);

        await writer.EncodeAsync(Key.AsMemory(), context, LengthFormat.LittleEndian, token)
            .ConfigureAwait(false);

        await writer.EncodeAsync(DictionaryKey.AsMemory(), context, LengthFormat.LittleEndian, token)
            .ConfigureAwait(false);
    }

#pragma warning disable CA2252
    public static async ValueTask<DeleteHashSetCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        using var keyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        using var dictKeyOwner = await reader.DecodeAsync(
            new DecodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian,
            token: token).ConfigureAwait(false);

        return new DeleteHashSetCommand
        {
            Key = new string(keyOwner.Span),
            DictionaryKey = new string(dictKeyOwner.Span)
        };
    }
}
