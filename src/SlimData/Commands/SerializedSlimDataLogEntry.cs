using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;

namespace SlimData.Commands;

internal readonly struct SerializedSlimDataLogEntry(
    int commandId,
    long term,
    ReadOnlyMemory<byte> payload,
    object? context) : IInputLogEntry
{
    public long Term => term;

    public int? CommandId => commandId;

    public bool IsReusable => true;

    public long? Length => payload.Length;

    public object? Context { get; init; } = context;

    public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : IAsyncBinaryWriter
        => writer.Invoke(payload, token);

    bool IDataTransferObject.TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = payload;
        return true;
    }

    internal static async ValueTask<SerializedSlimDataLogEntry> CreateAsync<TCommand>(
        TCommand command,
        long term,
        object? context,
        CancellationToken token)
        where TCommand : struct, ICommand<TCommand>
    {
        var transferObject = (IDataTransferObject)command;
        var declaredLength = transferObject.Length
            ?? throw new InvalidDataException($"{typeof(TCommand).Name} does not declare its serialized length.");
        SlimDataCommandCodec.ValidateCommandLength(declaredLength, typeof(TCommand).Name);

        var payload = await DataTransferObject.ToByteArrayAsync(command, null, token).ConfigureAwait(false);
        if (payload.LongLength != declaredLength)
        {
            throw new InvalidDataException(
                $"{typeof(TCommand).Name} serialized {payload.LongLength} bytes; expected {declaredLength}.");
        }

        SlimDataCommandCodec.ValidateCurrentEnvelope(payload, typeof(TCommand).Name);
        return new SerializedSlimDataLogEntry(TCommand.Id, term, payload, context);
    }
}
