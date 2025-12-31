using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using DotNext.Net.Cluster.Messaging;
using Microsoft.AspNetCore.Connections;
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

        // http factory stub
        var httpFactory = new TestHttpClientFactory(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

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

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object, httpFactory);

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

        repo.Verify(r => r.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BroadcastFilePutAsync_passes_expireAt_to_repo_when_ttl_provided()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        var httpFactory = new TestHttpClientFactory(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        bus.SetupGet(b => b.Members).Returns(Array.Empty<ISubscriber>());
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        long? capturedExpireAt = null;
        repo.Setup(r => r.SaveAsync("id1", It.IsAny<Stream>(), "text/plain", true, It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Stream, string, bool, long?, CancellationToken>((_, _, _, _, exp, _) => capturedExpireAt = exp)
            .ReturnsAsync(new FilePutResult("deadbeef", "text/plain", 4));

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object, httpFactory);

        var ttlMs = 5_000L;
        var before = DateTime.UtcNow.Ticks;
        await sut.BroadcastFilePutAsync("id1", new MemoryStream(Encoding.UTF8.GetBytes("test")), "text/plain", contentLengthBytes: 4, overwrite: true, ttl: ttlMs, CancellationToken.None);
        var after = DateTime.UtcNow.Ticks;

        await sut.DisposeAsync();

        Assert.NotNull(capturedExpireAt);
        var min = before + ttlMs * TimeSpan.TicksPerMillisecond - TimeSpan.TicksPerSecond;
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

        var httpFactory = new TestHttpClientFactory(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        remote1.SetupGet(m => m.IsRemote).Returns(true);
        remote2.SetupGet(m => m.IsRemote).Returns(true);

        remote1.SetupGet(m => m.EndPoint).Returns(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5001));
        remote2.SetupGet(m => m.EndPoint).Returns(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 5002));

        bus.SetupGet(b => b.Members).Returns(new List<ISubscriber> { remote1.Object, remote2.Object });
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        repo.Setup(r => r.SaveAsync("id1", It.IsAny<Stream>(), "text/plain", true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FilePutResult("deadbeef", "text/plain", 4));

        remote1.Setup(m => m.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("501", inner: null, statusCode: HttpStatusCode.NotImplemented));

        int called = 0;
        remote2.Setup(m => m.SendSignalAsync(It.IsAny<IMessage>(), true, It.IsAny<CancellationToken>()))
               .Callback(() => called++)
               .Returns(Task.CompletedTask);

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object, httpFactory);

        var result = await sut.BroadcastFilePutAsync(
            "id1",
            new MemoryStream(Encoding.UTF8.GetBytes("test")),
            "text/plain",
            contentLengthBytes: 4,
            overwrite: true,
            ttl: null,
            CancellationToken.None);

        Assert.Equal("deadbeef", result.Sha256Hex);
        Assert.Equal(1, called);
    }

    [Fact]
    public async Task PullFileIfMissingAsync_returns_local_stream_when_already_present()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);
        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        var httpFactory = new TestHttpClientFactory(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        bus.SetupGet(b => b.Members).Returns(Array.Empty<ISubscriber>());
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        repo.Setup(r => r.ExistsAsync("id1", "sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        repo.Setup(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object, httpFactory);

        var pulled = await sut.PullFileIfMissingAsync("id1", "sha", null, CancellationToken.None);

        Assert.NotNull(pulled.Stream);

        using var s = pulled.Stream!;
        var bytes = new byte[3];
        var read = s.Read(bytes, 0, 3);

        Assert.Equal(3, read);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task PullFileIfMissingAsync_downloads_from_first_remote_with_file_and_returns_local_stream()
    {
        var repo = new Mock<IFileRepository>(MockBehavior.Strict);
        var bus = new Mock<IMessageBus>(MockBehavior.Strict);

        var remote1 = new Mock<ISubscriber>(MockBehavior.Strict);
        var remote2 = new Mock<ISubscriber>(MockBehavior.Strict);

        var loggerMock = new Mock<ILogger<ClusterFileSync>>();
        var queue = new ClusterFileAnnounceQueue();

        remote1.SetupGet(m => m.IsRemote).Returns(true);
        remote2.SetupGet(m => m.IsRemote).Returns(true);

        remote1.SetupGet(m => m.EndPoint).Returns(new UriEndPoint(new Uri(
            $"http://{IPAddress.Loopback.ToString()}:{5001}")));
        remote2.SetupGet(m => m.EndPoint).Returns(new UriEndPoint(new Uri(
            $"http://{IPAddress.Loopback.ToString()}:{5002}")));

        bus.SetupGet(b => b.Members).Returns(new[] { remote1.Object, remote2.Object });
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        var payload = Encoding.UTF8.GetBytes("hello");
        var sha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var len = payload.Length;
        var expireAt = DateTime.UtcNow.AddMinutes(10).Ticks;

        // ExistsAsync appelé au moins 2 fois (avant/après lock)
        repo.SetupSequence(r => r.ExistsAsync("id1", sha, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(false);

        byte[] savedBytes = Array.Empty<byte>();
        string? savedContentType = null;
        long? savedExpireAt = null;

        repo.Setup(r => r.SaveAsync(
                "id1",
                It.IsAny<Stream>(),
                It.IsAny<string>(),
                true,
                It.IsAny<long?>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Stream, string, bool, long?, CancellationToken>(async (_, stream, ct, _, exp, token) =>
            {
                savedContentType = ct;
                savedExpireAt = exp;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, token);
                savedBytes = ms.ToArray();
                var sh = Convert.ToHexString(SHA256.HashData(savedBytes)).ToLowerInvariant();
                return new FilePutResult(sh, ct, savedBytes.Length);
            });

        repo.Setup(r => r.OpenReadAsync("id1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(savedBytes, writable: false));

        var handler = new ClusterFilesHttpHandler(
            sha: sha,
            payload: payload,
            expireAtUtcTicks: expireAt,
            portWithFile: 5002);

        var httpFactory = new TestHttpClientFactory(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object, httpFactory);

        var pulled = await sut.PullFileIfMissingAsync("id1", sha, null, CancellationToken.None);

        Assert.NotNull(pulled.Stream);

        using var s = pulled.Stream!;
        using var ms2 = new MemoryStream();
        await s.CopyToAsync(ms2);
        Assert.Equal(payload, ms2.ToArray());

        Assert.Equal("application/octet-stream", savedContentType);
        Assert.Equal(expireAt, savedExpireAt);
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
        remote.SetupGet(m => m.EndPoint).Returns(new UriEndPoint(new Uri(
            $"http://{IPAddress.Loopback.ToString()}:{5001}")));

        bus.SetupGet(b => b.Members).Returns(new[] { remote.Object });
        bus.Setup(b => b.AddListener(It.IsAny<IInputChannel>()));
        bus.Setup(b => b.RemoveListener(It.IsAny<IInputChannel>()));

        repo.Setup(r => r.ExistsAsync("id1", "sha", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var httpFactory = new TestHttpClientFactory(new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            Timeout = Timeout.InfiniteTimeSpan
        });

        var sut = new ClusterFileSync(bus.Object, repo.Object, queue, loggerMock.Object, httpFactory);

        var pulled = await sut.PullFileIfMissingAsync("id1", "sha", null, CancellationToken.None);

        Assert.Null(pulled.Stream);

        repo.Verify(r => r.SaveAsync(
            It.IsAny<string>(),
            It.IsAny<Stream>(),
            It.IsAny<string>(),
            It.IsAny<bool>(),
            It.IsAny<long?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------------- helpers ----------------

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_fn(request));
    }

    /// <summary>
    /// Simule /cluster/files/{id}?sha=... sur un port spécifique.
    /// - HEAD: 404 si mauvais port, sinon 200 + Content-Length + Content-Type + X-Expire
    /// - GET Range: 206 + body (slice)
    /// </summary>
    private sealed class ClusterFilesHttpHandler : HttpMessageHandler
    {
        private readonly string _sha;
        private readonly byte[] _payload;
        private readonly long _expireAt;
        private readonly int _portWithFile;

        public ClusterFilesHttpHandler(string sha, byte[] payload, long expireAtUtcTicks, int portWithFile)
        {
            _sha = sha;
            _payload = payload;
            _expireAt = expireAtUtcTicks;
            _portWithFile = portWithFile;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var port = request.RequestUri!.Port;

            // mauvais node => 404
            if (port != _portWithFile)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            if (request.Method == HttpMethod.Head)
            {
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                resp.Content.Headers.ContentLength = _payload.Length;
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                resp.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
                resp.Headers.TryAddWithoutValidation("X-SlimFaas-ExpireAtUtcTicks", _expireAt.ToString());
                return Task.FromResult(resp);
            }

            if (request.Method == HttpMethod.Get)
            {
                // Range demandé
                var range = request.Headers.Range;
                if (range is null || range.Ranges.Count != 1)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));

                var r = range.Ranges.First();
                var from = (int)(r.From ?? 0);
                var to = (int)(r.To ?? (_payload.Length - 1));
                if (from < 0) from = 0;
                if (to >= _payload.Length) to = _payload.Length - 1;
                if (to < from) to = from;

                var sliceLen = (to - from) + 1;
                var slice = new byte[sliceLen];
                Buffer.BlockCopy(_payload, from, slice, 0, sliceLen);

                var resp = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice)
                };
                resp.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                resp.Content.Headers.ContentLength = sliceLen;
                resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _payload.Length);
                resp.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
                resp.Headers.TryAddWithoutValidation("X-SlimFaas-ExpireAtUtcTicks", _expireAt.ToString());
                return Task.FromResult(resp);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        }
    }
}
