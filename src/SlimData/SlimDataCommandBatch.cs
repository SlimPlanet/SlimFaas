using MemoryPack;
using System.Buffers.Binary;
using SlimData.Commands;

namespace SlimData;

public enum SlimDataBatchOperationKind : byte
{
    KeyValue = 1,
    DeleteKeyValue = 2,
    AddHashSet = 3,
    DeleteHashSet = 4,
    ListLeftPush = 5,
    ListRightPop = 6,
    ListCallback = 7
}

[MemoryPackable]
public partial class SlimDataBatchOperation
{
    public SlimDataBatchOperationKind Kind { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public byte[] Value { get; set; } = [];
    public Dictionary<string, byte[]>? HashValues { get; set; }
    public string DictionaryKey { get; set; } = string.Empty;
    public KeyValueOperation KeyValueOperation { get; set; }
    public long IntegerDelta { get; set; }
    public decimal FloatDelta { get; set; }
    public long? ExpireAtUtcTicks { get; set; }
    public long NowTicks { get; set; }
    public string ElementId { get; set; } = string.Empty;
    public int RetryTimeoutSeconds { get; set; }
    public int[] Retries { get; set; } = [];
    public int[] HttpStatusRetries { get; set; } = [];
    public string TransactionId { get; set; } = string.Empty;
    public int Count { get; set; }
    public string[] ReservedIps { get; set; } = [];
    public QueueItemStatus[] CallbackItems { get; set; } = [];
}

[MemoryPackable]
public partial class SlimDataBatchOperationResult
{
    public SlimDataBatchOperationKind Kind { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool Applied { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ElementId { get; set; }
    public KeyValueCommandResult? KeyValueResult { get; set; }
    public QueueData[]? QueueItems { get; set; }
}

[MemoryPackable]
public partial class SlimDataCommandBatchRequest
{
    public string ProducerId { get; set; } = string.Empty;
    public string GenerationId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public SlimDataBatchOperation[] Operations { get; set; } = [];
}

[MemoryPackable]
public partial class SlimDataCommandBatchResponse
{
    public string ProducerId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public bool Duplicate { get; set; }
    public bool SequenceGap { get; set; }
    public long ExpectedSequence { get; set; }
    public string? ErrorMessage { get; set; }
    public SlimDataBatchOperationResult[] Results { get; set; } = [];
}

public sealed class SlimDataRaftBatchEnvelope
{
    public SlimDataCommandBatchRequest[] Requests { get; set; } = [];
}

[MemoryPackable]
public partial class SlimDataProducerSession
{
    public string GenerationId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public byte[] Response { get; set; } = [];
}

internal static class SlimDataCommandBatchValidator
{
    internal const string ProducerSessionKeyPrefix = "\0slimdata:producer-session:";
    internal const int MaxRequestsPerRaftBatch = 16;
    internal const int MaxOperationsPerRequest = SlimDataCommandCodec.MaxBatchItems;

    internal static void ValidateEnvelope(SlimDataRaftBatchEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var requests = envelope.Requests ?? [];
        if (requests.Length is < 1 or > MaxRequestsPerRaftBatch)
            throw new InvalidDataException($"A composite RAFT batch must contain between 1 and {MaxRequestsPerRaftBatch} requests.");

        foreach (var request in requests)
            ValidateRequest(request);
    }

    internal static void ValidateRequest(SlimDataCommandBatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdentifier(request.ProducerId, nameof(request.ProducerId));
        ValidateIdentifier(request.GenerationId, nameof(request.GenerationId));
        ValidateIdentifier(request.RequestId, nameof(request.RequestId));
        if (request.Sequence <= 0)
            throw new InvalidDataException("Batch sequence must be positive.");

        var operations = request.Operations ?? [];
        if (operations.Length is < 1 or > MaxOperationsPerRequest)
            throw new InvalidDataException($"A producer batch must contain between 1 and {MaxOperationsPerRequest} operations.");

        foreach (var operation in operations)
            ValidateOperation(operation);
    }

    private static void ValidateOperation(SlimDataBatchOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ValidateIdentifier(operation.RequestId, nameof(operation.RequestId));
        ValidateIdentifier(operation.Key, nameof(operation.Key));
        if (operation.Key.StartsWith(ProducerSessionKeyPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("The key uses a reserved SlimData prefix.");
        if (operation.Value is { Length: > SlimDataCommandCodec.MaxValueBytes })
            throw new InvalidDataException("Operation value exceeds the SlimData value limit.");

        switch (operation.Kind)
        {
            case SlimDataBatchOperationKind.KeyValue:
                if (!Enum.IsDefined(operation.KeyValueOperation))
                    throw new InvalidDataException("Unsupported key/value operation.");
                break;

            case SlimDataBatchOperationKind.AddHashSet:
                if (operation.HashValues is null)
                    throw new InvalidDataException("Hash values are required.");
                if (operation.HashValues.Count > SlimDataCommandCodec.MaxCollectionCount)
                    throw new InvalidDataException("Hashset item count exceeds the SlimData limit.");
                foreach (var (field, value) in operation.HashValues)
                {
                    ValidateIdentifier(field, "Hash field");
                    if (value is { Length: > SlimDataCommandCodec.MaxValueBytes })
                        throw new InvalidDataException("Hash value exceeds the SlimData value limit.");
                }
                break;

            case SlimDataBatchOperationKind.DeleteHashSet:
                if (!string.IsNullOrEmpty(operation.DictionaryKey))
                    ValidateIdentifier(operation.DictionaryKey, nameof(operation.DictionaryKey));
                break;

            case SlimDataBatchOperationKind.ListLeftPush:
                ValidateIdentifier(operation.ElementId, nameof(operation.ElementId));
                if (operation.Retries.Length > SlimDataCommandCodec.MaxCollectionCount ||
                    operation.HttpStatusRetries.Length > SlimDataCommandCodec.MaxCollectionCount)
                    throw new InvalidDataException("Queue retry configuration exceeds the SlimData limit.");
                break;

            case SlimDataBatchOperationKind.ListRightPop:
                ValidateIdentifier(operation.TransactionId, nameof(operation.TransactionId));
                if (operation.Count < 0 || operation.Count > SlimDataCommandCodec.MaxBatchItems)
                    throw new InvalidDataException("Queue pop count is outside the SlimData limit.");
                if (operation.ReservedIps.Length > SlimDataCommandCodec.MaxCollectionCount)
                    throw new InvalidDataException("Reserved IP count exceeds the SlimData limit.");
                foreach (var reservedIp in operation.ReservedIps)
                    ValidateIdentifier(reservedIp, "Reserved IP", allowEmpty: true);
                break;

            case SlimDataBatchOperationKind.ListCallback:
                if (operation.CallbackItems.Length > SlimDataCommandCodec.MaxCollectionCount)
                    throw new InvalidDataException("Callback item count exceeds the SlimData limit.");
                foreach (var callback in operation.CallbackItems)
                    ValidateIdentifier(callback.Id, "Callback item ID");
                break;

            case SlimDataBatchOperationKind.DeleteKeyValue:
                break;

            default:
                throw new InvalidDataException($"Unsupported SlimData batch operation kind {(byte)operation.Kind}.");
        }
    }

    private static void ValidateIdentifier(string? value, string name, bool allowEmpty = false)
    {
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{name} is required.");
        if (value is not null && System.Text.Encoding.UTF8.GetByteCount(value) > SlimDataCommandCodec.MaxStringBytes)
            throw new InvalidDataException($"{name} exceeds the SlimData string limit.");
    }
}

internal static class SlimDataRaftBatchEnvelopeCodec
{
    internal static byte[] Serialize(SlimDataRaftBatchEnvelope envelope)
    {
        SlimDataCommandBatchValidator.ValidateEnvelope(envelope);
        var requests = envelope.Requests;
        var payloads = new byte[requests.Length][];
        long length = sizeof(int);
        for (var i = 0; i < requests.Length; i++)
        {
            payloads[i] = MemoryPackSerializer.Serialize(requests[i]);
            length = checked(length + sizeof(int) + payloads[i].Length);
        }
        if (length > SlimDataCommandCodec.MaxValueBytes)
            throw new InvalidDataException("The composite SlimData RAFT payload is too large.");

        var result = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var offset = 0;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), payloads.Length);
        offset += sizeof(int);
        foreach (var payload in payloads)
        {
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), payload.Length);
            offset += sizeof(int);
            payload.CopyTo(result, offset);
            offset += payload.Length;
        }
        return result;
    }

    internal static SlimDataRaftBatchEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length < sizeof(int))
                throw new InvalidDataException("The composite SlimData RAFT payload is truncated.");
            var offset = 0;
            var requestCount = BinaryPrimitives.ReadInt32LittleEndian(payload);
            offset += sizeof(int);
            if (requestCount is < 1 or > SlimDataCommandBatchValidator.MaxRequestsPerRaftBatch)
                throw new InvalidDataException("The composite SlimData RAFT request count is invalid.");

            var requests = new SlimDataCommandBatchRequest[requestCount];
            for (var i = 0; i < requestCount; i++)
            {
                if (payload.Length - offset < sizeof(int))
                    throw new InvalidDataException("The composite SlimData RAFT payload is truncated.");
                var requestLength = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
                offset += sizeof(int);
                if (requestLength < 0 || requestLength > payload.Length - offset)
                    throw new InvalidDataException("The composite SlimData RAFT request length is invalid.");
                requests[i] = MemoryPackSerializer.Deserialize<SlimDataCommandBatchRequest>(
                                  payload.Slice(offset, requestLength))
                              ?? throw new InvalidDataException("A composite SlimData RAFT request is null.");
                offset += requestLength;
            }
            if (offset != payload.Length)
                throw new InvalidDataException("The composite SlimData RAFT payload has trailing bytes.");

            var envelope = new SlimDataRaftBatchEnvelope { Requests = requests };
            SlimDataCommandBatchValidator.ValidateEnvelope(envelope);
            return envelope;
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("The composite SlimData RAFT payload is invalid.", ex);
        }
    }
}
