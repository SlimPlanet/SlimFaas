using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;

namespace SlimData.Commands;

internal static class SerializedSlimDataLogEntry
{
    internal static BinaryLogEntry Create(
        int commandId,
        long term,
        ReadOnlyMemory<byte> payload,
        object? context,
        string commandName)
    {
        SlimDataCommandCodec.ValidateCommandLength(payload.Length, commandName);
        SlimDataCommandCodec.ValidateCurrentEnvelope(payload.Span, commandName);
        return new BinaryLogEntry
        {
            CommandId = commandId,
            Term = term,
            Content = payload,
            Context = context
        };
    }

    internal static async ValueTask<BinaryLogEntry> CreateAsync<TCommand>(
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

        return Create(TCommand.Id, term, payload, context, typeof(TCommand).Name);
    }
}
