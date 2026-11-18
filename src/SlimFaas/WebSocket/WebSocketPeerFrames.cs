using System.Net.WebSockets;
using System.Threading.Channels;

namespace SlimFaas.WebSocket;

internal sealed record WebSocketPeerFrame(WebSocketMessageType Type, byte[] Payload);

internal static class WebSocketPeerFrames
{
    internal const int MaximumFrameBytes = 256 * 1024;

    internal static async Task<WebSocketPeerFrame> ReceiveAsync(System.Net.WebSockets.WebSocket socket,
        string correlationId, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[32 * 1024];
        ValueWebSocketReceiveResult received;
        do
        {
            received = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (received.MessageType != System.Net.WebSockets.WebSocketMessageType.Binary)
                throw new IOException("WebSocket peer disconnected or sent a non-binary frame.");
            if (message.Length + received.Count > MaximumFrameBytes)
                throw new InvalidDataException("WebSocket peer frame exceeds the size limit.");
            await message.WriteAsync(buffer.AsMemory(0, received.Count), cancellationToken).ConfigureAwait(false);
        } while (!received.EndOfMessage);
        var bytes = message.ToArray();
        if (bytes.Length < BinaryFrame.HeaderSize)
            throw new InvalidDataException("Truncated WebSocket peer frame.");
        var header = BinaryFrame.DecodeHeader(bytes);
        if (header.CorrelationId != correlationId || header.PayloadLength != bytes.Length - BinaryFrame.HeaderSize)
            throw new InvalidDataException("Invalid WebSocket peer frame identity or length.");
        return new(header.Type, bytes[BinaryFrame.HeaderSize..]);
    }

    internal static Task SendAsync(System.Net.WebSockets.WebSocket socket, WebSocketMessageType type,
        string correlationId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        socket.SendAsync(BinaryFrame.Encode(type, correlationId, payload.Span).AsMemory(),
            System.Net.WebSockets.WebSocketMessageType.Binary, true, cancellationToken).AsTask();

    internal static async Task SendBodyAsync(System.Net.WebSockets.WebSocket socket, string correlationId,
        Stream? body, CancellationToken readToken, CancellationToken frameToken)
    {
        if (body is not null)
        {
            var buffer = new byte[32 * 1024];
            int read;
            while ((read = await body.ReadAsync(buffer, readToken).ConfigureAwait(false)) > 0)
                await SendAsync(socket, WebSocketMessageType.SyncRequestChunk, correlationId,
                    buffer.AsMemory(0, read), frameToken).ConfigureAwait(false);
        }
        await SendAsync(socket, WebSocketMessageType.SyncRequestEnd, correlationId, ReadOnlyMemory<byte>.Empty,
            frameToken).ConfigureAwait(false);
    }
}

internal sealed class WebSocketPeerBodyStream(ChannelReader<byte[]> reader) : Stream
{
    private ReadOnlyMemory<byte> _current;
    public override bool CanRead => true;
    public override bool CanWrite => false;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use asynchronous reads.");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (_current.IsEmpty)
        {
            if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
            if (reader.TryRead(out var chunk)) _current = chunk;
        }
        int length = Math.Min(buffer.Length, _current.Length);
        _current[..length].CopyTo(buffer);
        _current = _current[length..];
        return length;
    }
}
