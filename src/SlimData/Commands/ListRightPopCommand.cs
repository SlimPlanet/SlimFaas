using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Text;

namespace SlimData.Commands;

public struct ListRightPopCommand : ICommand<ListRightPopCommand>
{
    public const int Id = 19;
    static int ICommand<ListRightPopCommand>.Id => Id;

    public string Key { get; set; }
    public int Count { get; set; }
    public long NowTicks { get; set; }
    public string IdTransaction { get; set; }
    public List<string> ReservedIps { get; set; }

    long? IDataTransferObject.Length
    {
        get
        {
            var result = checked(
                SlimDataCommandCodec.HeaderLength +
                SlimDataCommandCodec.GetStringLength(Key) +
                sizeof(int) +
                sizeof(long) +
                SlimDataCommandCodec.GetStringLength(IdTransaction) +
                sizeof(int));
            foreach (var reservedIp in ReservedIps ?? [])
                result = checked(result + SlimDataCommandCodec.GetStringLength(reservedIp));
            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : notnull, IAsyncBinaryWriter
    {
        IAsyncBinaryWriter output = writer;
        SlimDataCommandCodec.ValidateCommandLength(
            ((IDataTransferObject)this).Length.GetValueOrDefault(),
            nameof(ListRightPopCommand));
        await SlimDataCommandCodec.WriteHeaderAsync(output, token).ConfigureAwait(false);
        await SlimDataCommandCodec.WriteStringAsync(output, Key, nameof(Key), token).ConfigureAwait(false);

        await output.WriteLittleEndianAsync(Count, token).ConfigureAwait(false);
        await output.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);

        await SlimDataCommandCodec.WriteStringAsync(output, IdTransaction, nameof(IdTransaction), token)
            .ConfigureAwait(false);

        var reservedIps = ReservedIps ?? [];
        await SlimDataCommandCodec.WriteCountAsync(
            output,
            reservedIps.Count,
            SlimDataCommandCodec.MaxCollectionCount,
            nameof(ReservedIps),
            token).ConfigureAwait(false);
        foreach (var ip in reservedIps)
        {
            await SlimDataCommandCodec.WriteStringAsync(output, ip, "Reserved IP", token).ConfigureAwait(false);
        }
    }

#pragma warning disable CA2252
    public static async ValueTask<ListRightPopCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        try
        {
            IAsyncBinaryReader input = reader;
            await SlimDataCommandCodec.ReadHeaderAsync(input, nameof(ListRightPopCommand), token)
                .ConfigureAwait(false);
            var key = await SlimDataCommandCodec.ReadStringAsync(input, nameof(Key), token).ConfigureAwait(false);
            var count = await input.ReadLittleEndianAsync<int>(token).ConfigureAwait(false);
            if (count < 0 || count > SlimDataCommandCodec.MaxCollectionCount)
                throw new InvalidDataException($"Invalid pop count {count}.");
            var nowTicks = await input.ReadLittleEndianAsync<long>(token).ConfigureAwait(false);
            var transactionId = await SlimDataCommandCodec.ReadStringAsync(input, nameof(IdTransaction), token)
                .ConfigureAwait(false);
            var reservedIpsCount = await SlimDataCommandCodec.ReadCountAsync(
                input,
                SlimDataCommandCodec.MaxCollectionCount,
                nameof(ReservedIps),
                token).ConfigureAwait(false);
            var reservedIps = new List<string>(reservedIpsCount);
            for (var i = 0; i < reservedIpsCount; i++)
            {
                reservedIps.Add(await SlimDataCommandCodec.ReadStringAsync(input, "Reserved IP", token)
                    .ConfigureAwait(false));
            }

            SlimDataCommandCodec.EnsureFullyConsumed(input, nameof(ListRightPopCommand));
            return new ListRightPopCommand
            {
                Key = key,
                Count = count,
                NowTicks = nowTicks,
                IdTransaction = transactionId,
                ReservedIps = reservedIps
            };
        }
        catch (Exception ex) when (SlimDataCommandCodec.IsStructuralException(ex) || ex is InvalidDataException)
        {
            throw SlimDataCommandCodec.WrapStructuralException(nameof(ListRightPopCommand), ex);
        }
    }
}
