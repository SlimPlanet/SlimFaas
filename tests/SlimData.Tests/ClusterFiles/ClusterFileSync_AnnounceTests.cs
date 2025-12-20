using System.Text;
using DotNext.Net.Cluster.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;
using Xunit;

public sealed class ClusterFileSync_AnnounceTests
{
    [Fact]
    public async Task ReceiveSignal_enqueue_announce_for_background_pull()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var logger = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        IInputChannel? captured = null;
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()))
            .Callback<IInputChannel>(ch => captured = ch);

        bus.SetupGet(b => b.Members).Returns(Array.Empty<ISubscriber>());
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, logger.Object);

        Assert.NotNull(captured);

        var id = "id1";
        var sha = "deadbeef";
        var len = 4L;
        var contentType = "text/plain";
        var overwrite = true;

        var idEnc = Base64UrlCodec.Encode(id);
        var announceName = FileSyncProtocol.BuildAnnounceName(idEnc, sha, len, contentType, overwrite);
        var signal = new TextMessage("", announceName);

        var sender = new Mock<ISubscriber>(MockBehavior.Strict);

        await captured!.ReceiveSignal(sender.Object, signal, context: null, token: CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        AnnouncedFile? item = null;

        await foreach (var a in queue.ReadAllAsync(cts.Token))
        {
            item = a;
            break;
        }

        Assert.NotNull(item);
        Assert.Equal(id, item!.Id);
        Assert.Equal(sha, item.Sha256Hex);

        // ✅ Verify minimal
        await sut.DisposeAsync();

        bus.Verify(b => b.AddListener(It.IsAny<IInputChannel>()), Times.Once);
        bus.Verify(b => b.RemoveListener(It.IsAny<IInputChannel>()), Times.Once); // grâce au await using
    }

}
