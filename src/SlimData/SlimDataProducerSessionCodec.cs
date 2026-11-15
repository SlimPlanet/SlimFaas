using System.Buffers;
using MemoryPack;

namespace SlimData;

internal readonly record struct SlimDataProducerSessionView(
    string? GenerationId, long Sequence, string? RequestId, ReadOnlyMemory<byte> Response);

internal static class SlimDataProducerSessionCodec
{
    private const byte CurrentMemberCount = 4;
    private const int LargeResponseBytes = 65536;

    internal static byte[] Serialize(SlimDataProducerSession session)
    {
        if (session.Response is null || session.Response.Length < LargeResponseBytes)
            return MemoryPackSerializer.Serialize(session);

        // The byte-array length header has a fixed size. Measure only the small
        // metadata, then let the generated formatter write the unchanged format
        // directly into its final array. A second pooled copy of a large dequeue
        // response can otherwise exhaust the managed heap during Raft apply.
        byte[] header = MemoryPackPayload.Serialize(new SlimDataProducerSession
        {
            GenerationId = session.GenerationId,
            Sequence = session.Sequence,
            RequestId = session.RequestId
        });
        if (header[0] != CurrentMemberCount)
            return MemoryPackSerializer.Serialize(session);
        byte[] payload = GC.AllocateUninitializedArray<byte>(checked(header.Length + session.Response.Length));
        var buffer = new ExactBufferWriter(payload);
        using var optionalState = MemoryPackWriterOptionalStatePool.Rent(null);
        var writer = new MemoryPackWriter<ExactBufferWriter>(ref buffer, optionalState);
        writer.WritePackable(session);
        writer.Flush();
        if (buffer.WrittenCount != payload.Length)
            throw new InvalidDataException("The producer session formatter wrote an unexpected length.");
        return payload;
    }

    internal static SlimDataProducerSessionView? Deserialize(ReadOnlyMemory<byte> payload)
    {
        using var optionalState = MemoryPackReaderOptionalStatePool.Rent(null);
        using var reader = new MemoryPackReader(payload.Span, optionalState);
        if (!reader.TryReadObjectHeader(out byte memberCount))
            return null;
        if (memberCount != CurrentMemberCount)
            return DeserializeLegacy(payload);

        string? generation = reader.ReadString();
        reader.ReadUnmanaged(out long sequence);
        string? request = reader.ReadString();
        if (!reader.TryReadCollectionHeader(out int responseBytes) || responseBytes < 0 ||
            reader.Remaining != responseBytes)
        {
            // Preserve the original reader's handling of older shapes, null
            // arrays and trailing bytes, including its structural failures.
            return DeserializeLegacy(payload);
        }

        // Ordinary next-sequence requests need the metadata, not a copy of the
        // previous reply. Duplicate replies decode directly from this owned slice.
        return new SlimDataProducerSessionView(
            generation, sequence, request, payload.Slice(reader.Consumed, responseBytes));
    }

    private static SlimDataProducerSessionView? DeserializeLegacy(ReadOnlyMemory<byte> payload)
    {
        SlimDataProducerSession? session = MemoryPackPayload.Deserialize<SlimDataProducerSession>(payload.Span);
        return session is null ? null : new SlimDataProducerSessionView(
            session.GenerationId, session.Sequence, session.RequestId, session.Response);
    }

    private sealed class ExactBufferWriter(byte[] payload) : IBufferWriter<byte>
    {
        internal int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, payload.Length - WrittenCount);
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return payload.AsMemory(WrittenCount);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            ValidateSizeHint(sizeHint);
            return payload.AsSpan(WrittenCount);
        }

        private void ValidateSizeHint(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint > payload.Length - WrittenCount || WrittenCount == payload.Length)
                throw new InvalidOperationException("The producer session formatter exceeded its measured buffer.");
        }
    }
}
