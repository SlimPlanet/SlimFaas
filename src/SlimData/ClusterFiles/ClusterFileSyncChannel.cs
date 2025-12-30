using System.Net.Mime;
using DotNext.Net.Cluster.Messaging;

namespace SlimData.ClusterFiles;

internal sealed class ClusterFileSyncChannel(ClusterFileAnnounceQueue announceQueue) : IInputChannel
{
    public bool IsSupported(string messageName, bool oneWay)
        => oneWay && messageName.StartsWith(FileSyncProtocol.AnnouncePrefix + "|", StringComparison.Ordinal);

    public Task ReceiveSignal(ISubscriber sender, IMessage signal, object? context, CancellationToken token)
    {
        if (FileSyncProtocol.TryParseAnnounceName(signal.Name, out var idEnc, out var sha, out _, out _, out _))
            announceQueue.TryEnqueue(new AnnouncedFile(Base64UrlCodec.Decode(idEnc), sha));
        return Task.CompletedTask;
    }

    public Task<IMessage> ReceiveMessage(ISubscriber sender, IMessage message, object? context, CancellationToken token)
        => Task.FromResult<IMessage>(new TextMessage("", "slimfaas.file.fetch.notfound")); // ou throw/not supported
}
