using System.Security.Cryptography;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class FileContentHashTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(65536)]
    [InlineData(131071)]
    [InlineData(131072)]
    [InlineData(131073)]
    [InlineData(1048576)]
    [InlineData(4194304)]
    public async Task Fragmented_nonseekable_reads_match_the_framework_hash_without_closing_the_source(int length)
    {
        byte[] content = new byte[length];
        for (int index = 0; index < content.Length; index++) content[index] = (byte)(index % 251);
        using var source = new FragmentedStream(content);

        byte[] actual = await FileContentHash.ComputeSha256Async(source, CancellationToken.None);

        Assert.Equal(SHA256.HashData(content), actual);
        Assert.True(source.CanRead);
        Assert.Equal(-1, source.ReadByte());
    }

    [Fact]
    public async Task A_pre_cancelled_read_does_not_touch_the_source()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var source = new FailingStream();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileContentHash.ComputeSha256Async(source, cancellation.Token).AsTask());

        Assert.Equal(0, source.ReadCalls);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Cancellation_at_end_of_input_cannot_return_a_successful_hash()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new EndCancellationStream(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileContentHash.ComputeSha256Async(source, cancellation.Token).AsTask());

        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task An_interrupted_read_propagates_without_closing_the_callers_stream()
    {
        using var source = new FailingStream();

        await Assert.ThrowsAsync<IOException>(() =>
            FileContentHash.ComputeSha256Async(source, CancellationToken.None).AsTask());

        Assert.Equal(1, source.ReadCalls);
        Assert.True(source.CanRead);
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 257)], cancellationToken);
    }

    private sealed class FailingStream : MemoryStream
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return ValueTask.FromException<int>(new IOException("Interrupted body verification."));
        }
    }

    private sealed class EndCancellationStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await cancellation.CancelAsync();
            return 0;
        }
    }
}
