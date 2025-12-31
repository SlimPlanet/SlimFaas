// ClusterFileAnnounceQueue.cs
using System.Threading.Channels;

namespace SlimData.ClusterFiles;

public sealed record AnnouncedFile(string Id, string Sha256Hex, string? PreferredNode);

public sealed class ClusterFileAnnounceQueue
{
    private readonly Channel<AnnouncedFile> _channel = Channel.CreateBounded<AnnouncedFile>(
        new BoundedChannelOptions(capacity: 1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public bool TryEnqueue(AnnouncedFile item) => _channel.Writer.TryWrite(item);

    public IAsyncEnumerable<AnnouncedFile> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}