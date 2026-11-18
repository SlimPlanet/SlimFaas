using System.Runtime.InteropServices;
using MemoryPack;

namespace SlimData.Tests;

public sealed class SlimDataProducerSessionCodecTests
{
    [Theory]
    [InlineData(64)]
    [InlineData(65536)]
    [InlineData(1048576)]
    public void Reading_a_session_borrows_the_persisted_response_instead_of_copying_it(int responseBytes)
    {
        var session = CreateSession(responseBytes);
        byte[] encoded = MemoryPackSerializer.Serialize(session);
        byte[] storage = new byte[encoded.Length + 14];
        encoded.CopyTo(storage, 7);

        SlimDataProducerSessionView view = SlimDataProducerSessionCodec.Deserialize(
            new ReadOnlyMemory<byte>(storage, 7, encoded.Length))
            ?? throw new InvalidOperationException("A persisted session must decode.");

        Assert.Equal(session.GenerationId, view.GenerationId);
        Assert.Equal(session.Sequence, view.Sequence);
        Assert.Equal(session.RequestId, view.RequestId);
        Assert.Equal(session.Response, view.Response.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(view.Response, out ArraySegment<byte> response));
        Assert.Same(storage, response.Array);
        Assert.Equal(7 + encoded.Length - responseBytes, response.Offset);
        Assert.Equal(responseBytes, response.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(65535)]
    [InlineData(65536)]
    [InlineData(1048576)]
    public void Written_sessions_remain_byte_compatible_with_the_original_MemoryPack_format(int responseBytes)
    {
        var session = CreateSession(responseBytes);
        byte[] expected = MemoryPackSerializer.Serialize(session);

        byte[] actual = SlimDataProducerSessionCodec.Serialize(session);

        Assert.Equal(expected, actual);
        SlimDataProducerSession restored = MemoryPackPayload.Deserialize<SlimDataProducerSession>(actual)
            ?? throw new InvalidOperationException("The original reader must accept the written session.");
        Assert.Equal(session.GenerationId, restored.GenerationId);
        Assert.Equal(session.Sequence, restored.Sequence);
        Assert.Equal(session.RequestId, restored.RequestId);
        Assert.Equal(session.Response, restored.Response);
    }

    [Fact]
    public void A_null_persisted_session_keeps_its_original_meaning()
    {
        byte[] encoded = MemoryPackSerializer.Serialize<SlimDataProducerSession>(null);

        Assert.Null(SlimDataProducerSessionCodec.Deserialize(encoded));
    }

    [Fact]
    public void A_truncated_response_is_rejected_instead_of_becoming_a_partial_cached_reply()
    {
        byte[] encoded = MemoryPackSerializer.Serialize(CreateSession(65536));

        Assert.Throws<MemoryPackSerializationException>(() =>
            SlimDataProducerSessionCodec.Deserialize(encoded.AsMemory(0, encoded.Length - 1)));
    }

    private static SlimDataProducerSession CreateSession(int responseBytes) => new()
    {
        GenerationId = "generation-\u03bb-\u65e5\u672c",
        Sequence = 42,
        RequestId = "request-\u00e9-\U0001f680",
        Response = Enumerable.Range(0, responseBytes).Select(static index => (byte)(index % 251)).ToArray()
    };
}
