using System.Net.Mime;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

internal sealed class ClusterFileSyncChannel(IFileRepository repo, ClusterFileAnnounceQueue announceQueue, KeyedAsyncLock idLock, ILogger<ClusterFileSync>  logger) : IInputChannel
{
    public bool IsSupported(string messageName, bool oneWay)
    {
        if (oneWay)
            return messageName.StartsWith(FileSyncProtocol.AnnouncePrefix + "|", StringComparison.Ordinal);

        return messageName.StartsWith(FileSyncProtocol.FetchPrefix + "|", StringComparison.Ordinal);
    }

    public Task ReceiveSignal(ISubscriber sender, IMessage signal, object? context, CancellationToken token)
    {
        // Variante "announce-only": on ne transfère PAS le fichier ici.
        // On pourrait stocker une "hint" si tu veux plus tard (index des versions), mais version simple => no-op.
        if (FileSyncProtocol.TryParseAnnounceName(signal.Name, out var idEnc, out var sha, out _, out _, out _))
        {
            var id = Base64UrlCodec.Decode(idEnc);
            announceQueue.TryEnqueue(new AnnouncedFile(id, sha));
        }
        return Task.CompletedTask;
    }
    

    public async Task<IMessage> ReceiveMessage(
        ISubscriber sender,
        IMessage message,
        object? context,
        CancellationToken token)
    {
        Stream? stream = null;
        KeyedAsyncLock.Releaser releaser = default;
        bool hasReleaser = false;
        try
        {
            if (!FileSyncProtocol.TryParseFetchName(message.Name, out var idEnc, out var sha))
                return new TextMessage("", FileSyncProtocol.FetchNotFound);

            var id = Base64UrlCodec.Decode(idEnc);

            if (!await repo.ExistsAsync(id, sha, token).ConfigureAwait(false))
                return new TextMessage("", FileSyncProtocol.FetchNotFound);

            var meta = await repo.TryGetMetadataAsync(id, token).ConfigureAwait(false);
            if (meta is null)
                return new TextMessage("", FileSyncProtocol.FetchNotFound);
            
            releaser = await idLock.AcquireAsync(id, meta.Length, token).ConfigureAwait(false);
            hasReleaser = true;

            stream = await repo.OpenReadAsync(id, token).ConfigureAwait(false);
            stream = new LockReleasingStream(stream, releaser);
            hasReleaser = false; // ownership transferred to LockReleasingStream

            var replyName = FileSyncProtocol.BuildFetchOkName(idEnc, sha, meta.Length, meta.ExpireAtUtcTicks);
            var type = new ContentType(meta.ContentType);

            var msgOut = new StreamMessage(stream, leaveOpen: false, name: replyName, type: type);
            
            return msgOut;
        }
        catch(Exception ex)
        {
            logger.LogWarning($"[FileSync] Fetch handler failed: {ex}");
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            else if (hasReleaser)
                await releaser.DisposeAsync().ConfigureAwait(false);
            return new TextMessage("", FileSyncProtocol.FetchNotFound);
        }
    }

}

internal sealed class LockReleasingStream(Stream inner, KeyedAsyncLock.Releaser releaser) : Stream
{
    private Stream? _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private KeyedAsyncLock.Releaser _releaser = releaser;

    public override bool CanRead => _inner!.CanRead;
    public override bool CanSeek => _inner!.CanSeek;
    public override bool CanWrite => _inner!.CanWrite;
    public override long Length => _inner!.Length;
    public override long Position { get => _inner!.Position; set => _inner!.Position = value; }

    public override void Flush() => _inner!.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _inner!.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => _inner!.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner!.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => _inner!.Seek(offset, origin);
    public override void SetLength(long value) => _inner!.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner!.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _inner!.WriteAsync(buffer, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        var inner = Interlocked.Exchange(ref _inner, null);
        try { inner?.Dispose(); }
        finally { _releaser.Dispose(); }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var inner = Interlocked.Exchange(ref _inner, null);
        try
        {
            if (inner is not null)
                await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await _releaser.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}