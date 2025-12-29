using System.Net;
using System.Net.Http;
using System.Net.Mime;
using System.Security.Cryptography;
using System.Text;
using DotNext.Net.Cluster.Messaging;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;
using Xunit;

public sealed class ClusterFileSyncTests
{
    [Fact]
    public async Task BroadcastFilePutAsync_saves_and_broadcasts_announce_only()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);

        var remote1 = new Mock<ISubscriber>(MockBehavior.Strict);
        var remote2 = new Mock<ISubscriber>(MockBehavior.Strict);
        var local = new Mock<ISubscriber>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();

        var queue = new ClusterFileAnnounceQueue();

        remote1.SetupGet(m => m.IsRemote).Returns(true);
        remote2.SetupGet(m => m.IsRemote).Returns(true);
        local.SetupGet(m => m.IsRemote).Returns(false);

        bus.SetupGet(b => b.Members).Returns(new List<ISubscriber> { local.Object, remote1.Object, remote2.Object });
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        var put = new FilePutResult("deadbeef", "text/plain", 4);

        repo.Setup(r => r.SaveAsync("id1", It.IsAny<Stream>(), "text/plain", true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(put);

        var names = new List<string>();

        remote1.Setup(m => m.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
               .Callback<IMessage, bool, CancellationToken>((msg, _, _) => names.Add(msg.Name))
               .Returns(Task.CompletedTask);

        remote2.Setup(m => m.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
               .Callback<IMessage, bool, CancellationToken>((msg, _, _) => names.Add(msg.Name))
               .Returns(Task.CompletedTask);

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object);

        var result = await sut.BroadcastFilePutAsync(
            "id1",
            new MemoryStream(Encoding.UTF8.GetBytes("test")),
            "text/plain",
            contentLengthBytes: 4,
            overwrite: true,
            ttl: null,
            CancellationToken.None);

        Assert.Equal("deadbeef", result.Sha256Hex);
        Assert.Equal(2, names.Count);
        Assert.All(names, n => Assert.StartsWith(FileSyncProtocol.AnnouncePrefix + "|", n, StringComparison.Ordinal));

        // broadcast-only => on ne relit jamais le fichier pour l'envoyer
        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BroadcastFilePutAsync_passes_expireAt_to_repo_when_ttl_provided()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        bus.SetupGet(b => b.Members).Returns(Array.Empty<ISubscriber>());
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        long? capturedExpireAt = null;
        repo.Setup(r => r.SaveAsync("id1", It.IsAny<Stream>(), "text/plain", true, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, bool, long?, CancellationToken>((_, _, _, _, exp, _) => capturedExpireAt = exp)
            .ReturnsAsync(new FilePutResult("deadbeef", "text/plain", 4));

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object);

        var ttlMs = 5_000L;
        var before = DateTime.UtcNow.Ticks;
        await sut.BroadcastFilePutAsync("id1", new MemoryStream(Encoding.UTF8.GetBytes("test")), "text/plain", contentLengthBytes: 4, true, ttlMs, CancellationToken.None);
        var after = DateTime.UtcNow.Ticks;

        await sut.DisposeAsync();

        Assert.NotNull(capturedExpireAt);
        var min = before + ttlMs * TimeSpan.TicksPerMillisecond - TimeSpan.TicksPerSecond; // tolérance
        var max = after + ttlMs * TimeSpan.TicksPerMillisecond + TimeSpan.TicksPerSecond;
        Assert.InRange(capturedExpireAt!.Value, min, max);

        repo.VerifyAll();
        bus.VerifyAll();
    }

    [Fact]
    public async Task BroadcastFilePutAsync_does_not_throw_when_a_remote_returns_501()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);

        var remote1 = new Mock<ISubscriber>(MockBehavior.Strict);
        var remote2 = new Mock<ISubscriber>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();

        var queue = new ClusterFileAnnounceQueue();

        remote1.SetupGet(m => m.IsRemote).Returns(true);
        remote2.SetupGet(m => m.IsRemote).Returns(true);

        remote1.SetupGet(m => m.EndPoint).Returns(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5001));
        remote2.SetupGet(m => m.EndPoint).Returns(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5002));

        bus.SetupGet(b => b.Members).Returns(new List<ISubscriber> { remote1.Object, remote2.Object });
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        var put = new FilePutResult("deadbeef", "text/plain", 4);

        repo.Setup(r => r.SaveAsync("id1", It.IsAny<Stream>(), "text/plain", true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(put);

        // remote1 => 501 Not Implemented
        remote1.Setup(m => m.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("Response status code does not indicate success: 501 (Not Implemented).",
                                                     inner: null,
                                                     statusCode: HttpStatusCode.NotImplemented));

        // remote2 => OK
        int called = 0;
        remote2.Setup(m => m.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
               .Callback(() => called++)
               .Returns(Task.CompletedTask);

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object);

        // Act + Assert: ne doit pas throw
        var result = await sut.BroadcastFilePutAsync(
            "id1",
            new MemoryStream(Encoding.UTF8.GetBytes("test")),
            "text/plain",
            contentLengthBytes: 4,
            overwrite: true,
            null,
            CancellationToken.None);

        Assert.Equal("deadbeef", result.Sha256Hex);
        Assert.Equal(1, called); // remote2 doit quand même être appelé
    }

    [Fact]
    public async Task PullFileIfMissingAsync_returns_local_stream_when_already_present()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        bus.SetupGet(b => b.Members).Returns(Array.Empty<ISubscriber>());
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        repo.Setup(r => r.ExistsAsync("id1", "sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        repo.Setup(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object);

        var pulled = await sut.PullFileIfMissingAsync("id1", "sha", CancellationToken.None);

        Assert.NotNull(pulled.Stream);

        using var s = pulled.Stream!;
        var bytes = new byte[3];
        var read = s.Read(bytes, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task PullFileIfMissingAsync_downloads_from_first_remote_with_matching_sha_and_returns_local_stream()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);

        var remote1 = new Mock<ISubscriber>(MockBehavior.Strict);
        var remote2 = new Mock<ISubscriber>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        remote1.SetupGet(m => m.IsRemote).Returns(true);
        remote2.SetupGet(m => m.IsRemote).Returns(true);

        bus.SetupGet(b => b.Members).Returns(new[] { remote1.Object, remote2.Object });

        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        var payload = Encoding.UTF8.GetBytes("hello");
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var len = payload.Length;

        repo.Setup(r => r.ExistsAsync("id1", sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var expireAt = DateTime.UtcNow.AddMinutes(10).Ticks;
        long? capturedExpireAt = null;
        repo.Setup(r => r.SaveFromTransferObjectAsync(
                "id1",
                It.IsAny<DotNext.IO.IDataTransferObject>(),
                It.IsAny<string>(),
                true,
                sha,
                len,
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, DotNext.IO.IDataTransferObject, string, bool, string?, long?, long?, CancellationToken>((_, _, _, _, _, _, exp, _) => capturedExpireAt = exp)
            .ReturnsAsync(new FilePutResult(sha, "application/octet-stream", len));

        // après download, on renvoie un stream local (simulé)
        repo.Setup(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(payload));

        // remote1 répond OK + stream
        remote1.Setup(m => m.SendMessageAsync(
                It.IsAny<IMessage>(),
                It.IsAny<MessageReader<bool>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IMessage, MessageReader<bool>, CancellationToken>((req, reader, ct) =>
            {
                var idEnc = Base64UrlCodec.Encode("id1");
                var replyName = FileSyncProtocol.BuildFetchOkName(idEnc, sha, len, expireAt);
                var reply = new StreamMessage(new MemoryStream(payload), leaveOpen: false, name: replyName, type: new ContentType("application/octet-stream"));
                return reader(reply, ct).AsTask();
            });

        // remote2 ne doit pas être appelé (on s'arrête au 1er qui a le fichier)
        remote2.Setup(m => m.SendMessageAsync(
                It.IsAny<IMessage>(),
                It.IsAny<MessageReader<bool>>(),
                It.IsAny<CancellationToken>()))
            .Throws(new Exception("Should not be called"));

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object);

        var pulled = await sut.PullFileIfMissingAsync("id1", sha, CancellationToken.None);

        Assert.NotNull(pulled.Stream);

        using var s = pulled.Stream!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        Assert.Equal(payload, ms.ToArray());

        repo.Verify(r => r.SaveFromTransferObjectAsync(
            "id1",
            It.IsAny<DotNext.IO.IDataTransferObject>(),
            It.IsAny<string>(),
            true,
            sha,
            len,
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.Equal(expireAt, capturedExpireAt);
    }

    [Fact]
    public async Task PullFileIfMissingAsync_returns_null_stream_if_no_member_has_file()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);

        var remote = new Mock<ISubscriber>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        remote.SetupGet(m => m.IsRemote).Returns(true);

        bus.SetupGet(b => b.Members).Returns(new[] { remote.Object });

        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        repo.Setup(r => r.ExistsAsync("id1", "sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        remote.Setup(m => m.SendMessageAsync(
                It.IsAny<IMessage>(),
                It.IsAny<MessageReader<bool>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IMessage, MessageReader<bool>, CancellationToken>((req, reader, ct) =>
            {
                var reply = new TextMessage("", FileSyncProtocol.FetchNotFound);
                return reader(reply, ct).AsTask();
            });

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object);

        var pulled = await sut.PullFileIfMissingAsync("id1", "sha", CancellationToken.None);

        Assert.Null(pulled.Stream);

        repo.Verify(r => r.SaveFromTransferObjectAsync(
            It.IsAny<string>(),
            It.IsAny<DotNext.IO.IDataTransferObject>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<string?>(),
            It.IsAny<long?>(),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }
}
