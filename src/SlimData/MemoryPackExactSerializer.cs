using System.Buffers;
using MemoryPack;

namespace SlimData;

internal static class MemoryPackExactSerializer
{
    internal static byte[] Serialize<T>(T value) where T : IMemoryPackable<T>
    {
        int length;
        using (var measurement = new MeasuringBufferWriter())
        {
            Write(measurement, value);
            length = measurement.WrittenCount;
        }
        return Serialize(value, length);
    }

    internal static byte[] Serialize<T>(T value, int length) where T : IMemoryPackable<T>
    {
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        using var buffer = new ExactBufferWriter(payload);
        Write(buffer, value);
        if (buffer.WrittenCount != payload.Length)
            throw new InvalidDataException("The MemoryPack formatter wrote an unexpected length.");
        return payload;
    }

    private static void Write<TBuffer, T>(TBuffer buffer, T value)
        where TBuffer : IBufferWriter<byte>
        where T : IMemoryPackable<T>
    {
        using var optionalState = MemoryPackWriterOptionalStatePool.Rent(null);
        var writer = new MemoryPackWriter<TBuffer>(ref buffer, optionalState);
        writer.WritePackable(value);
        writer.Flush();
    }

    // Counting discards each committed span. Scratch storage grows only to the
    // largest formatter write, rather than the combined dequeue response size.
    private sealed class MeasuringBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);
        internal int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _buffer.Length);
            WrittenCount = checked(WrittenCount + count);
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer;
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return _buffer;
        }

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
            if (sizeHint <= _buffer.Length)
                return;
            byte[] replacement = ArrayPool<byte>.Shared.Rent(sizeHint);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = replacement;
        }

        public void Dispose()
        {
            if (_buffer.Length == 0)
                return;
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }
    }

    private sealed class ExactBufferWriter(byte[] payload) : IBufferWriter<byte>, IDisposable
    {
        private byte[]? _scratch;
        private bool _writingScratch;
        internal int WrittenCount { get; private set; }

        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, payload.Length - WrittenCount);
            if (_writingScratch)
                _scratch.AsSpan(0, count).CopyTo(payload.AsSpan(WrittenCount));
            WrittenCount += count;
            _writingScratch = false;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => Prepare(sizeHint);

        public Span<byte> GetSpan(int sizeHint = 0) => Prepare(sizeHint).Span;

        private Memory<byte> Prepare(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            if (sizeHint <= payload.Length - WrittenCount && WrittenCount < payload.Length)
            {
                _writingScratch = false;
                return payload.AsMemory(WrittenCount);
            }

            // A formatter can reserve its maximum UTF-8 string length and then
            // commit fewer bytes. Honor that reservation without padding the
            // output or copying the entire response: stage only this tail.
            if (_scratch is null || _scratch.Length < sizeHint)
            {
                byte[] replacement = ArrayPool<byte>.Shared.Rent(Math.Max(1, sizeHint));
                if (_scratch is not null)
                    ArrayPool<byte>.Shared.Return(_scratch);
                _scratch = replacement;
            }
            _writingScratch = true;
            return _scratch;
        }

        public void Dispose()
        {
            if (_scratch is not null)
                ArrayPool<byte>.Shared.Return(_scratch);
            _scratch = null;
        }
    }
}
