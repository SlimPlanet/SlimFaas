using MemoryPack;

namespace SlimFaas.Tests.Workers;

public sealed class QueuedHttpRequestCodecTests
{
    [Theory]
    [InlineData(65536)]
    [InlineData(1048576)]
    public async Task Large_inline_body_borrows_the_queue_packet_without_another_payload_array(int bodyBytes)
    {
        CustomRequest original = Request(bodyBytes);
        byte[] packet = MemoryPackSerializer.Serialize(original);

        var decoded = QueuedHttpRequestCodec.DeserializeForDispatch(packet);
        using Stream? body = decoded.BodyStream;

        Assert.Null(decoded.Request.Body);
        var stream = Assert.IsType<MemoryStream>(body);
        Assert.False(stream.CanWrite);
        Assert.Equal(0, stream.Position);
        Assert.Equal(bodyBytes, stream.Length);
        Assert.True(stream.TryGetBuffer(out ArraySegment<byte> segment));
        Assert.Same(packet, segment.Array);
        Assert.True(segment.Offset > 0);
        Assert.Equal(bodyBytes, segment.Count);
        using var observed = new MemoryStream();
        await stream.CopyToAsync(observed);
        Assert.Equal(original.Body, observed.ToArray());
        Assert.Equal(packet, MemoryPackSerializer.Serialize(decoded.Request with { Body = original.Body }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(65535)]
    public void Small_requests_keep_the_existing_body_and_stream_behavior(int bodyBytes)
    {
        CustomRequest original = Request(bodyBytes);
        byte[] packet = MemoryPackSerializer.Serialize(original);

        var decoded = QueuedHttpRequestCodec.DeserializeForDispatch(packet);
        using Stream? body = decoded.BodyStream;

        Assert.Null(body);
        Assert.Equal(original.Body, decoded.Request.Body);
        Assert.Equal(packet, MemoryPackSerializer.Serialize(decoded.Request));
    }

    [Fact]
    public void File_references_keep_the_offloaded_body_path()
    {
        CustomRequest original = Request(65536) with { OffloadedFileId = "file-reference" };
        byte[] packet = MemoryPackSerializer.Serialize(original);

        var decoded = QueuedHttpRequestCodec.DeserializeForDispatch(packet);
        using Stream? body = decoded.BodyStream;

        Assert.Null(body);
        Assert.Equal(packet, MemoryPackSerializer.Serialize(decoded.Request));
    }

    [Fact]
    public void Truncated_metadata_does_not_dispatch_a_partial_request()
    {
        byte[] packet = MemoryPackSerializer.Serialize(Request(65536));
        Assert.Throws<MemoryPackSerializationException>(() =>
            QueuedHttpRequestCodec.DeserializeForDispatch(packet.AsSpan(0, packet.Length - 10).ToArray()));
    }

    private static CustomRequest Request(int bodyBytes) => new(
        [new("Content-Type", ["application/octet-stream"]), new("X-Multi", [null, "\u00e9-\u65e5\u672c"])],
        Enumerable.Range(0, bodyBytes).Select(static value => (byte)(value % 251)).ToArray(),
        "function-\u03bb", "/work/\u00e9", "POST", "?value=\U0001f680");
}
