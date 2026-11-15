using System.Net;
using System.Net.Http.Headers;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Messaging;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class FileCopyProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimdata_copy_protocol_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("node")]
    [InlineData("volume")]
    [InlineData("not-persisted")]
    public void A_receipt_for_another_identity_or_an_unpersisted_copy_is_rejected(string changed)
    {
        var local = new FilePersistenceReceipt("file", new string('a', 64), 3, Guid.NewGuid().ToString("N"));
        var remote = new FileCopyReceipt("file", local.Sha256Hex, 3, "remote", Guid.NewGuid().ToString("N"), true);
        Assert.True(FileCopyProtocol.Matches(local, "remote", remote));
        remote = changed switch
        {
            "id" => remote with { Id = "another" },
            "hash" => remote with { Sha256Hex = new string('b', 64) },
            "length" => remote with { Length = 4 },
            "node" => remote with { NodeIdentity = "another" },
            "volume" => remote with { StorageIdentity = local.StorageIdentity },
            _ => remote with { Persisted = false }
        };
        Assert.False(FileCopyProtocol.Matches(local, "remote", remote));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_known_peer_confirms_only_verified_persisted_bytes(bool corrupt)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        byte[] bytes = [1, 2, 3];
        var source = Repository("a");
        var target = Repository("b");
        using var body = new MemoryStream(bytes);
        FilePutResult stored = await source.SaveAsync("file", body, "text/plain", false, null, CancellationToken.None);
        var aLocal = Member(5001, remote: false);
        var aAtB = Member(5001, remote: true);
        var bLocal = Member(5002, remote: false);
        var bAtA = Member(5002, remote: true);
        IInputChannel? channel = null;
        var busB = Bus([bLocal.Object, aAtB.Object]);
        busB.Setup(value => value.AddListener(It.IsAny<IInputChannel>())).Callback<IInputChannel>(value => channel = value);
        using var client = new HttpClient(new SourceHandler(bytes, corrupt));
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient("ClusterFilesTransfer")).Returns(client);
        await using var receiver = new ClusterFileSync(busB.Object, target, new ClusterFileAnnounceQueue(),
            NullLogger<ClusterFileSync>.Instance, factory.Object);
        Assert.NotNull(channel);
        bAtA.Setup(value => value.SendMessageAsync<FileCopyReceipt>(It.IsAny<IMessage>(),
                It.IsAny<MessageReader<FileCopyReceipt>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IMessage message, MessageReader<FileCopyReceipt> reader, CancellationToken token) =>
            {
                IMessage reply = await channel!.ReceiveMessage(aAtB.Object, message, null, token);
                return await reader(reply, token);
            });
        var busA = Bus([aLocal.Object, bAtA.Object]);
        await using var sender = new ClusterFileSync(busA.Object, source, new ClusterFileAnnounceQueue(),
            NullLogger<ClusterFileSync>.Instance, Mock.Of<IHttpClientFactory>());

        if (corrupt)
        {
            await Assert.ThrowsAsync<IOException>(() => sender.ConfirmRemoteCopyAsync("file", stored.Sha256Hex, stored.Length, CancellationToken.None));
            Assert.False(await target.ExistsAsync("file", stored.Sha256Hex, CancellationToken.None));
            return;
        }
        FileCopyReceipt receipt = await sender.ConfirmRemoteCopyAsync("file", stored.Sha256Hex, stored.Length, CancellationToken.None);

        Assert.True(receipt.Persisted);
        Assert.Equal(bLocal.Object.Id.ToString(), receipt.NodeIdentity);
        Assert.Equal(target.StorageIdentity, receipt.StorageIdentity);
        Assert.NotEqual(source.StorageIdentity, receipt.StorageIdentity);
        Assert.True(await target.ExistsAsync("file", stored.Sha256Hex, CancellationToken.None));
        bAtA.Verify(value => value.SendMessageAsync<FileCopyReceipt>(It.IsAny<IMessage>(),
            It.IsAny<MessageReader<FileCopyReceipt>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_announcement_confirmation_cannot_satisfy_a_persisted_copy_request()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var repository = Repository("source");
        using var body = new MemoryStream(new byte[] { 1, 2, 3 });
        FilePutResult put = await repository.SaveAsync("file", body, "text/plain", false, null, CancellationToken.None);
        var local = Member(5001, remote: false);
        var remote = Member(5002, remote: true);
        remote.Setup(value => value.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        remote.Setup(value => value.SendMessageAsync<FileCopyReceipt>(It.IsAny<IMessage>(),
                It.IsAny<MessageReader<FileCopyReceipt>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FileCopyReceipt("file", put.Sha256Hex, 3, remote.Object.Id.ToString(),
                Guid.NewGuid().ToString("N"), false, "Only queued an announcement"));
        await using var sync = new ClusterFileSync(Bus([local.Object, remote.Object]).Object, repository,
            new ClusterFileAnnounceQueue(), NullLogger<ClusterFileSync>.Instance, Mock.Of<IHttpClientFactory>());

        await Assert.ThrowsAsync<IOException>(() => sync.ConfirmRemoteCopyAsync("file", put.Sha256Hex, 3, CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_copy_requests_are_rejected_before_the_copy_handler_runs()
    {
        bool called = false;
        var channel = new ClusterFileSyncChannel(new ClusterFileAnnounceQueue(), (_, _, _) =>
        {
            called = true;
            return Task.FromResult(new FileCopyReceipt("", "", 0, "", "", false));
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => channel.ReceiveMessage(Mock.Of<ISubscriber>(),
            new TextMessage(new string('x', 4097), FileCopyProtocol.RequestName), null, CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task A_peer_on_shared_declared_storage_is_rejected_before_any_download()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;
        var source = new DiskFileRepository(Path.Combine(_directory, "a"), NullLogger<DiskFileRepository>.Instance,
            dropPageCache: false, storageRoot: _directory);
        var target = new DiskFileRepository(Path.Combine(_directory, "b"), NullLogger<DiskFileRepository>.Instance,
            dropPageCache: false, storageRoot: _directory);
        var local = Member(5002, remote: false);
        var remote = Member(5001, remote: true);
        var bus = Bus([local.Object, remote.Object]);
        IInputChannel? channel = null;
        bus.Setup(value => value.AddListener(It.IsAny<IInputChannel>())).Callback<IInputChannel>(value => channel = value);
        var factory = new Mock<IHttpClientFactory>(MockBehavior.Strict);
        await using var receiver = new ClusterFileSync(bus.Object, target, new ClusterFileAnnounceQueue(),
            NullLogger<ClusterFileSync>.Instance, factory.Object);
        Assert.NotNull(channel);
        var request = FileCopyProtocol.Request(new FileCopyRequest("file", new string('a', 64), 3, source.StorageIdentity));

        IMessage reply = await channel!.ReceiveMessage(remote.Object, request, null, CancellationToken.None);
        FileCopyReceipt receipt = await FileCopyProtocol.ReadReceiptAsync(reply, CancellationToken.None);

        Assert.False(receipt.Persisted);
        Assert.Equal("Shared storage identity.", receipt.Error);
        factory.VerifyNoOtherCalls();
    }

    private DiskFileRepository Repository(string node) =>
        new(Path.Combine(_directory, node), NullLogger<DiskFileRepository>.Instance, dropPageCache: false);

    private static Mock<ISubscriber> Member(int port, bool remote)
    {
        var member = new Mock<ISubscriber>();
        member.SetupGet(value => value.IsRemote).Returns(remote);
        member.SetupGet(value => value.Id).Returns(ClusterMemberId.FromEndPoint(new IPEndPoint(IPAddress.Loopback, port)));
        member.SetupGet(value => value.EndPoint).Returns(new UriEndPoint(new Uri("http://127.0.0.1:" + port)));
        return member;
    }

    private static Mock<IMessageBus> Bus(ISubscriber[] members)
    {
        var bus = new Mock<IMessageBus>();
        bus.SetupGet(value => value.Members).Returns(members);
        return bus;
    }

    private sealed class SourceHandler(byte[] bytes, bool corrupt = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(request.Method == HttpMethod.Head ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(request.Method == HttpMethod.Head ? Array.Empty<byte>()
                    : corrupt ? bytes.Reverse().ToArray() : bytes)
            };
            response.Content.Headers.ContentLength = bytes.Length;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            if (request.Method == HttpMethod.Get)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, bytes.Length - 1, bytes.Length);
            return Task.FromResult(response);
        }
    }
}
