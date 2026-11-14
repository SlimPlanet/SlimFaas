using Xunit;

namespace SlimFaasClient.Tests;

public sealed class SyncResponseWriterChunkingTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(65_536, false)]
    [InlineData(65_537, false)]
    [InlineData(262_144, false)]
    [InlineData(2_097_152, false)]
    [InlineData(4_194_304, false)]
    [InlineData(0, true)]
    [InlineData(65_536, true)]
    [InlineData(65_537, true)]
    [InlineData(262_144, true)]
    [InlineData(2_097_152, true)]
    [InlineData(4_194_304, true)]
    public async Task LargeWrites_PreserveBytesAndFitTheCrossNodePeerFrameLimit(int length, bool memoryOverload)
    {
        const int offset = 37;
        byte[] buffer = new byte[length + offset + 11];
        for (var index = 0; index < buffer.Length; index++)
            buffer[index] = (byte)(index % 251);
        using var received = new MemoryStream();
        var frames = new List<int>();
        var events = new List<string>();
        await using var writer = new SyncResponseWriter("correlation",
            (_, _, _) => { events.Add("start"); return Task.CompletedTask; },
            async (_, chunk, token) =>
            {
                frames.Add(chunk.Length + BinaryFrame.HeaderSize);
                events.Add("chunk");
                await received.WriteAsync(chunk, token);
            },
            (_, _) => { events.Add("end"); return Task.CompletedTask; });

        if (memoryOverload)
            await writer.WriteAsync(buffer.AsMemory(offset, length));
        else
#pragma warning disable CA1835 // Exercise the public byte-array Stream overload as well as ReadOnlyMemory.
            await writer.WriteAsync(buffer, offset, length, CancellationToken.None);
#pragma warning restore CA1835
        await writer.CompleteAsync();

        Assert.Equal(buffer.AsSpan(offset, length).ToArray(), received.ToArray());
        // The cluster peer's complete binary frame is bounded at 256 KiB.
        Assert.All(frames, frame => Assert.InRange(frame, BinaryFrame.HeaderSize + 1, 256 * 1024));
        Assert.Equal("start", events[0]);
        Assert.Equal("end", events[^1]);
        Assert.True(writer.IsCompleted);
    }

    [Fact]
    public async Task CancellationBetweenChunks_StopsSendingWithoutCompletingTheResponse()
    {
        using var stopping = new CancellationTokenSource();
        var chunks = 0;
        var ends = 0;
        await using var writer = new SyncResponseWriter("correlation",
            (_, _, _) => Task.CompletedTask,
            (_, _, _) => { chunks++; stopping.Cancel(); return Task.CompletedTask; },
            (_, _) => { ends++; return Task.CompletedTask; });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await writer.WriteAsync(new byte[2_097_152].AsMemory(), stopping.Token));

        Assert.Equal(1, chunks);
        Assert.Equal(0, ends);
        Assert.False(writer.IsCompleted);
    }
}
