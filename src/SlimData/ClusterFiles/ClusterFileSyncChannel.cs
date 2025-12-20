using System.Net.Mime;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

internal sealed class ClusterFileSyncChannel(IFileRepository repo, ClusterFileAnnounceQueue announceQueue) : IInputChannel
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

            stream = await repo.OpenReadAsync(id, token).ConfigureAwait(false);

            var replyName = FileSyncProtocol.BuildFetchOkName(idEnc, sha, meta.Length, meta.ExpireAtUtcTicks);
            var type = new ContentType(meta.ContentType);

            var msgOut = new StreamMessage(stream, leaveOpen: false, name: replyName, type: type);
            
            return msgOut;
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            
            return new TextMessage("", FileSyncProtocol.FetchNotFound);
        }
    }

}
