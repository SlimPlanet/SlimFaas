using MemoryPack;

namespace SlimFaas;

internal static class QueuedHttpRequestCodec
{
    private const byte CurrentMemberCount = 7;
    private const int LargeBodyBytes = 65536;

    internal static (CustomRequest Request, Stream? BodyStream) DeserializeForDispatch(byte[] payload)
    {
        if (payload.Length < LargeBodyBytes)
            return DeserializeLegacy(payload);

        using var optionalState = MemoryPackReaderOptionalStatePool.Rent(null);
        using var reader = new MemoryPackReader(payload, optionalState);
        if (!reader.TryReadObjectHeader(out byte memberCount) || memberCount != CurrentMemberCount)
            return DeserializeLegacy(payload);

        List<CustomHeader>? headers = null;
        if (reader.TryReadCollectionHeader(out int headerCount))
        {
            if (headerCount < 0)
                return DeserializeLegacy(payload);
            headers = new List<CustomHeader>(headerCount);
            for (var index = 0; index < headerCount; index++)
                headers.Add(reader.ReadPackable<CustomHeader>());
        }

        if (!reader.TryReadCollectionHeader(out int bodyBytes) || bodyBytes < LargeBodyBytes)
            return DeserializeLegacy(payload);
        int bodyOffset = reader.Consumed;
        reader.Advance(bodyBytes);
        string? functionName = reader.ReadString();
        string? path = reader.ReadString();
        string? method = reader.ReadString();
        string? query = reader.ReadString();
        string? offloadedFileId = reader.ReadString();
        if (reader.Remaining != 0 || !string.IsNullOrEmpty(offloadedFileId))
            return DeserializeLegacy(payload);

        // QueueData owns this immutable packet throughout dispatch. Borrow its
        // inline body instead of cloning every body in the dequeued batch before
        // any HTTP request starts. The normal request bookkeeping owns the stream.
        var request = new CustomRequest(headers!, null, functionName!, path!, method!, query!, offloadedFileId);
        return (request, new MemoryStream(payload, bodyOffset, bodyBytes, writable: false, publiclyVisible: true));
    }

    private static (CustomRequest Request, Stream? BodyStream) DeserializeLegacy(byte[] payload)
        => (MemoryPackSerializer.Deserialize<CustomRequest>(payload), null);
}
