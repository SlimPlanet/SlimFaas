using MemoryPack;

namespace SlimData.Tests;

[CollectionDefinition("MemoryPack formatter registration", DisableParallelization = true)]
public sealed class MemoryPackRegistrationCollection;

[Collection("MemoryPack formatter registration")]
public sealed class MemoryPackReplayTests
{
    [Theory]
    [InlineData(SlimDataBatchOperationKind.None)]
    [InlineData(SlimDataBatchOperationKind.WebSocketDirectory)]
    [InlineData(SlimDataBatchOperationKind.Stream)]
    [InlineData(SlimDataBatchOperationKind.DeadLetter)]
    public void Valid_replay_does_not_depend_on_a_partially_published_root_formatter(SlimDataBatchOperationKind target)
    {
        SlimDataBatchOperation operation = CreateOperation(target);
        byte[] payload = SlimDataRaftBatchEnvelopeCodec.Serialize(new()
        {
            Requests = [new() { ProducerId = "owner", GenerationId = "generation", Sequence = 1,
                RequestId = "request", Operations = [operation] }]
        });
        // MemoryPack registration publishes its registered flag before assigning
        // the cached formatter. Model that observed startup window deterministically.
        // Disable parallelization so no other test sees the injected cache state.
        Action restore = SuspendFormatter(target);
        try
        {
            SlimDataRaftBatchEnvelope restored = SlimDataRaftBatchEnvelopeCodec.Deserialize(payload);
            SlimDataBatchOperation recovered = Assert.Single(Assert.Single(restored.Requests).Operations);
            Assert.Equal(operation.Kind, recovered.Kind);
            Assert.Equal(operation.Value, recovered.Value);
        }
        finally
        {
            restore();
        }
    }

    private static SlimDataBatchOperation CreateOperation(SlimDataBatchOperationKind target)
    {
        byte[] value = [42];
        string key = "key";
        if (target == SlimDataBatchOperationKind.WebSocketDirectory)
        {
            key = WebSocketDirectoryStore.Key;
            value = WebSocketDirectoryStore.SerializeMutation(new() { Action = WebSocketDirectoryAction.StartNode,
                Node = "node", Incarnation = Guid.NewGuid().ToString("N"),
                RelaySecret = Convert.ToBase64String(new byte[32]), NowTicks = DateTime.UtcNow.Ticks });
        }
        else if (target == SlimDataBatchOperationKind.Stream)
        {
            key = DurableStreamStore.DefinitionPrefix + "orders";
            value = MemoryPackPayload.Serialize(new DurableStreamMutation { Action = DurableStreamAction.Configure,
                Stream = "orders", Visibility = "Public", NewId = Guid.NewGuid().ToString("N"), NowTicks = DateTime.UtcNow.Ticks });
        }
        else if (target == SlimDataBatchOperationKind.DeadLetter)
        {
            key = "Queue:billing";
            value = MemoryPackPayload.Serialize(new DeadLetterMutation { Action = DeadLetterAction.Delete,
                Function = "billing", MessageId = "message", NowTicks = DateTime.UtcNow.Ticks });
        }
        return new() { Kind = target == SlimDataBatchOperationKind.None ? SlimDataBatchOperationKind.KeyValue : target,
            RequestId = "operation", Key = key, Value = value };
    }

    private static Action SuspendFormatter(SlimDataBatchOperationKind target) => target switch
    {
        SlimDataBatchOperationKind.WebSocketDirectory => SuspendFormatter<WebSocketDirectoryMutation>(),
        SlimDataBatchOperationKind.Stream => SuspendFormatter<DurableStreamMutation>(),
        SlimDataBatchOperationKind.DeadLetter => SuspendFormatter<DeadLetterMutation>(),
        _ => SuspendFormatter<SlimDataCommandBatchRequest>()
    };

    private static Action SuspendFormatter<T>() where T : IMemoryPackFormatterRegister
    {
        MemoryPackFormatterProvider.Register<T>();
        using var state = MemoryPackReaderOptionalStatePool.Rent(null);
        using var reader = new MemoryPackReader(ReadOnlySpan<byte>.Empty, state);
        MemoryPackFormatter<T> formatter = (MemoryPackFormatter<T>)reader.GetFormatter<T>();
        MemoryPackFormatterProvider.Register<T>(null!);
        return () => MemoryPackFormatterProvider.Register(formatter);
    }
}
