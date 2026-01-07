using System.Net.Mime;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

internal sealed class ClusterFileSyncChannel(ClusterFileAnnounceQueue announceQueue) : IInputChannel
{
    public bool IsSupported(string messageName, bool oneWay)
        => oneWay && messageName.StartsWith(FileSyncProtocol.AnnouncePrefix + "|", StringComparison.Ordinal);

    public Task ReceiveSignal(ISubscriber sender, IMessage signal, object? context, CancellationToken token)
    {
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

    public Task<IMessage> ReceiveMessage(ISubscriber sender, IMessage message, object? context, CancellationToken token)
        => Task.FromResult<IMessage>(new TextMessage("", "slimfaas.file.fetch.notfound")); // ou throw/not supported
}
