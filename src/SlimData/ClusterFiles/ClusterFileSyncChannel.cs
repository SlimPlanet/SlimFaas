using System.Net.Mime;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

internal sealed class ClusterFileSyncChannel(ClusterFileAnnounceQueue announceQueue,
    Func<ISubscriber, FileCopyRequest, CancellationToken, Task<FileCopyReceipt>>? confirmCopy = null) : IInputChannel
{
    public bool IsSupported(string messageName, bool oneWay)
        => !oneWay && confirmCopy is not null && messageName == FileCopyProtocol.RequestName || oneWay &&
           (messageName.StartsWith(FileSyncProtocol.AnnouncePrefix + "|", StringComparison.Ordinal) ||
            messageName.StartsWith(FileSyncProtocol.DeletePrefix + "|", StringComparison.Ordinal));

    public Task ReceiveSignal(ISubscriber sender, IMessage signal, object? context, CancellationToken token)
    {
        if (FileSyncProtocol.TryParseDeleteName(signal.Name, out string deleteIdEncoded))
        {
            announceQueue.TryEnqueue(new AnnouncedFile(
                Base64UrlCodec.Decode(deleteIdEncoded),
                string.Empty,
                null,
                Delete: true));
            return Task.CompletedTask;
        }

        if (FileSyncProtocol.TryParseAnnounceName(signal.Name, out var idEnc, out var sha, out _, out _, out _))
        {
            var id = Base64UrlCodec.Decode(idEnc);
            
            string? preferredNode = null;
            if (sender is IClusterMember cm)
            {
                preferredNode = cm.EndPoint?.ToString() ?? null;
            }
            
            announceQueue.TryEnqueue(new AnnouncedFile(id, sha, preferredNode));
        }

        return Task.CompletedTask;
    }

    public async Task<IMessage> ReceiveMessage(ISubscriber sender, IMessage message, object? context, CancellationToken token)
    {
        if (confirmCopy is null || message.Name != FileCopyProtocol.RequestName)
            return new TextMessage("", "slimfaas.file.fetch.notfound");
        FileCopyRequest request = await FileCopyProtocol.ReadRequestAsync(message, token).ConfigureAwait(false);
        FileCopyReceipt receipt = await confirmCopy(sender, request, token).ConfigureAwait(false);
        return FileCopyProtocol.Reply(receipt);
    }
}
