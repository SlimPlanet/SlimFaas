using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DotNext.Net.Cluster.Messaging;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Moq;
using SlimData.ClusterFiles;

namespace SlimData.Tests.ClusterFiles;

public sealed class ClusterFilePeerFallbackTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slimdata_peer_fallback_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("disconnect")]
    [InlineData("peer-timeout")]
    [InlineData("io")]
    [InlineData("stalled-head")]
    public async Task An_unavailable_first_peer_does_not_hide_a_verified_copy_on_the_next_peer(string failure)
    {
        using var handler = new PeerHandler(failure);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        await using var sync = CreateSync(repository, client);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        FilePullResult pulled = await sync.PullFileIfMissingAsync("needed", handler.Checksum, null, deadline.Token);

        Assert.NotNull(pulled.Stream);
        await using Stream body = pulled.Stream!;
        using var bytes = new MemoryStream();
        await body.CopyToAsync(bytes, deadline.Token);
        Assert.Equal(handler.Payload, bytes.ToArray());
        Assert.True(await repository.ExistsAsync("needed", handler.Checksum, deadline.Token));
        Assert.Equal(2, handler.HealthyRequests);
    }

    [Fact]
    public async Task Caller_cancellation_stops_peer_search_without_publishing_a_copy()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new PeerHandler("caller-cancel", cancellation);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        await using var sync = CreateSync(repository, client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sync.PullFileIfMissingAsync("needed", handler.Checksum, null, cancellation.Token));

        Assert.Equal(0, handler.HealthyRequests);
        Assert.False(await repository.ExistsAsync("needed", handler.Checksum, CancellationToken.None));
    }

    [Fact]
    public async Task No_available_peer_returns_no_body_and_does_not_publish_partial_bytes()
    {
        using var handler = new PeerHandler("disconnect", failAll: true);
        using var client = new HttpClient(handler);
        var repository = new DiskFileRepository(_directory, Mock.Of<ILogger<DiskFileRepository>>(), dropPageCache: false);
        await using var sync = CreateSync(repository, client);

        FilePullResult pulled = await sync.PullFileIfMissingAsync("needed", handler.Checksum, null, CancellationToken.None);

        Assert.Null(pulled.Stream);
        Assert.False(await repository.ExistsAsync("needed", handler.Checksum, CancellationToken.None));
    }

    private static ClusterFileSync CreateSync(IFileRepository repository, HttpClient client)
    {
        var bus = new Mock<IMessageBus>();
        bus.SetupGet(value => value.Members).Returns(new[] { Member("unavailable.invalid"), Member("healthy.invalid") });
        return new ClusterFileSync(bus.Object, repository, new ClusterFileAnnounceQueue(),
            Mock.Of<ILogger<ClusterFileSync>>(), new ClientFactory(client));
    }

    private static ISubscriber Member(string host)
    {
        var member = new Mock<ISubscriber>();
        member.SetupGet(value => value.IsRemote).Returns(true);
        member.SetupGet(value => value.EndPoint).Returns(new UriEndPoint(new Uri("http://" + host + "/raft")));
        return member.Object;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class PeerHandler(string failure, CancellationTokenSource? caller = null, bool failAll = false) : HttpMessageHandler
    {
        public byte[] Payload { get; } = [1, 2, 3, 4, 5];
        public string Checksum => Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
        public int HealthyRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failAll || request.RequestUri!.Host == "unavailable.invalid")
            {
                switch (failure)
                {
                    case "peer-timeout": throw new OperationCanceledException("Peer timed out independently of caller.");
                    case "io": throw new IOException("Peer closed transport.");
                    case "caller-cancel":
                        await caller!.CancelAsync();
                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    case "stalled-head":
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        break;
                    default: throw new HttpRequestException("First peer is unavailable.");
                }
            }
            HealthyRequests++;
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                response.Content.Headers.ContentLength = Payload.Length;
                return response;
            }
            var range = Assert.Single(request.Headers.Range!.Ranges);
            long start = range.From!.Value;
            long end = Math.Min(range.To!.Value, Payload.Length - 1);
            var result = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(Payload, (int)start, (int)(end - start + 1))
            };
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Payload.Length);
            return result;
        }
    }
}
