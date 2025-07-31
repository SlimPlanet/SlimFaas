using System.Text;
using DotNext.IO;
using DotNext.Runtime.Serialization;
using DotNext.Text;

namespace SlimData.Commands;

public record CallbackElement(string Identifier, int HttpCode);

public struct ListCallbackCommand() : ISerializable<ListCallbackCommand>
{
    public const int Id = 15;
    public string Key { get; set; }
    
    public long NowTicks { get; set; }
    public IList<CallbackElement> CallbackElements { get; set; }
    
    long? IDataTransferObject.Length // optional implementation, may return null
    {
        get
        {
            // compute length of the serialized data, in bytes
            long result = Encoding.UTF8.GetByteCount(Key);
            result += sizeof(long);
            result += sizeof(int); // 4 bytes for count
            foreach (var element in CallbackElements)
                result += Encoding.UTF8.GetByteCount(element.Identifier) + sizeof(int);
            return result;
        }
    }

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token) where TWriter : notnull, IAsyncBinaryWriter
    {
        var command = this;
        await writer.EncodeAsync(command.Key.AsMemory(), new EncodingContext(Encoding.UTF8, false),
            LengthFormat.LittleEndian, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(command.NowTicks, token).ConfigureAwait(false);
        await writer.WriteLittleEndianAsync(command.CallbackElements.Count, token).ConfigureAwait(false);
        foreach (var element in command.CallbackElements)
        {
            await writer.EncodeAsync(element.Identifier.AsMemory(), new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian, token).ConfigureAwait(false);
            await writer.WriteLittleEndianAsync(element.HttpCode, token).ConfigureAwait(false);
           
        }
    }

    public static async ValueTask<ListCallbackCommand> ReadFromAsync<TReader>(TReader reader, CancellationToken token) where TReader : notnull, IAsyncBinaryReader
    {
        var key = await reader.DecodeAsync( new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
        var nowTicks = await reader.ReadLittleEndianAsync<Int64>(token).ConfigureAwait(false);
        var callbackElementsCount = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
        var callbackElements = new List<CallbackElement>(callbackElementsCount);
        while (callbackElementsCount-- > 0)
        {
            var identifier = await reader.DecodeAsync( new DecodingContext(Encoding.UTF8, false), LengthFormat.LittleEndian, token: token).ConfigureAwait(false);
            var httpCode = await reader.ReadLittleEndianAsync<Int32>(token).ConfigureAwait(false);
            
            callbackElements.Add(new CallbackElement(identifier.ToString(), httpCode));
        }
        return new ListCallbackCommand
        {
            Key = key.ToString(),
            NowTicks = nowTicks,
            CallbackElements = callbackElements
        };
    }
}