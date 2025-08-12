using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Runtime.Serialization;
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
    
    public List<int> HttpStatusCodesWorthRetrying  { get; set; }
    
    public ReadOnlyMemory<byte> Value { get; set; }

    long? IDataTransferObject.Length => Encoding.UTF8.GetByteCount(Key)  + Value.Length + Encoding.UTF8.GetByteCount(Identifier) + sizeof(long) + sizeof(int) + Retries.Count * sizeof(int) + sizeof(int) + sizeof(int) + HttpStatusCodesWorthRetrying.Count * sizeof(int);

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : IAsyncBinaryWriter
    {
        var command = this;
        await writer.EncodeAsync(command.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
        await writer.EncodeAsync(command.Identifier.AsMemory(), new EncodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(NowTicks, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(RetryTimeout, token).ConfigureAwait(false);
        await writer.WriteAsync(command.Value, LengthFormat.Compressed, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(Retries.Count, token).ConfigureAwait(false);
        foreach (var retry in Retries)
        {
            await writer.WriteLittleEndianAsync(retry, token).ConfigureAwait(false);
        }
        await writer.WriteLittleEndianAsync(HttpStatusCodesWorthRetrying.Count, token).ConfigureAwait(false);
        foreach (var httpStatus in HttpStatusCodesWorthRetrying)
        {
            await writer.WriteLittleEndianAsync(httpStatus, token).ConfigureAwait(false);
        }
        
    }

#pragma warning disable CA2252
    public static async ValueTask<ListLeftPushCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token)
#pragma warning restore CA2252
        where TReader : notnull, IAsyncBinaryReader
    {
        var key = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
        var identifier = await reader.DecodeAsync(new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
        var nowTicks = await reader.ReadLittleEndianAsync<Int64>(token).ConfigureAwait(false);
        var timeout = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        using var value = await reader.ReadAsync(LengthFormat.Compressed, token: token).ConfigureAwait(false);
        var retriesCount = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var retries = new List<int>(retriesCount);
        while (retriesCount-- > 0)
        {
            retries.Add(await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false));
        }
        var httpStatusCodesWorthRetryingCount = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var httpStatusCodesWorthRetrying = new List<int>(httpStatusCodesWorthRetryingCount);
        while (httpStatusCodesWorthRetryingCount-- > 0)
        {
            httpStatusCodesWorthRetrying.Add(await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false));
        }
        
        return new ListLeftPushCommand
        {
            Key = key.ToString(),
            Identifier = identifier.ToString(),
            NowTicks = nowTicks,
            RetryTimeout = timeout,
            Retries = retries,
            Value = value.Memory.ToArray(),
            HttpStatusCodesWorthRetrying = httpStatusCodesWorthRetrying
        };
    }
}