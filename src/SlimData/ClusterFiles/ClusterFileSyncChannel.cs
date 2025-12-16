using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

internal sealed class ClusterFileSyncChannel : IInputChannel
{
    private readonly IFileRepository _repo;

    public ClusterFileSyncChannel(IFileRepository repo) => _repo = repo;

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
        _ = FileSyncProtocol.TryParseAnnounceName(signal.Name, out _, out _, out _, out _, out _);
        return Task.CompletedTask;
    }

    public async Task<IMessage> ReceiveMessage(ISubscriber sender, IMessage message, object? context, CancellationToken token)
    {
        try
        {
            if (!FileSyncProtocol.TryParseFetchName(message.Name, out var idEnc, out var sha))
                return new TextMessage("", FileSyncProtocol.FetchNotFound);

            var id = Base64UrlCodec.Decode(idEnc);

            if (!await _repo.ExistsAsync(id, sha, token).ConfigureAwait(false))
                return new TextMessage("", FileSyncProtocol.FetchNotFound);

            var meta = await _repo.TryGetMetadataAsync(id, token).ConfigureAwait(false);
            if (meta is null)
                return new TextMessage("", FileSyncProtocol.FetchNotFound);

            var stream = await _repo.OpenReadAsync(id, token).ConfigureAwait(false);
            var replyName = FileSyncProtocol.BuildFetchOkName(idEnc, sha, meta.Length);
            var type = new ContentType(meta.ContentType);

            return new StreamMessage(stream, leaveOpen: false, name: replyName, type: type);
        }
        catch
        {
            return new TextMessage("", FileSyncProtocol.FetchNotFound);
        }
    }
}
