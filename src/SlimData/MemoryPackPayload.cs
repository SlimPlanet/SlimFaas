using System.Buffers;
using MemoryPack;

namespace SlimData;

internal static class MemoryPackPayload
{
    internal static T? Deserialize<T>(ReadOnlySpan<byte> payload) where T : IMemoryPackable<T>
    {
        using var optionalState = MemoryPackReaderOptionalStatePool.Rent(null);
        using var reader = new MemoryPackReader(payload, optionalState);
        // Static generated dispatch avoids a root formatter being observed while
        // registration has published its flag but not yet assigned the formatter.
        return reader.ReadPackable<T>();
    }

    internal static byte[] Serialize<T>(T value) where T : IMemoryPackable<T>
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var optionalState = MemoryPackWriterOptionalStatePool.Rent(null);
        var writer = new MemoryPackWriter<ArrayBufferWriter<byte>>(ref buffer, optionalState);
        // Avoid generic virtual formatter dispatch, which can crash Native AOT at the root payload.
        writer.WritePackable(value);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
