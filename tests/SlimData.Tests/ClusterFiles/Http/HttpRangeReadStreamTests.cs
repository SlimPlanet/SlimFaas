using System.Net;
using SlimData.ClusterFiles.Http;

namespace SlimData.Tests.ClusterFiles.Http;

public sealed class HttpRangeReadStreamTests
{
    [Fact]
    public async Task Truncated_range_resumes_at_the_last_received_byte_without_replaying_the_prefix()
    {
        var offsets = new List<long>();
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            long offset = request.Headers.Range!.Ranges.Single().From!.Value;
            offsets.Add(offset);
            byte[] bytes = offset == 0 ? [1, 2] : [3, 4, 5];
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(bytes) };
        }));
        await using var stream = new HttpRangeReadStream(client, new Uri("http://node/cluster/files/id"), 5, 5, TimeSpan.FromSeconds(1));
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, destination.ToArray());
        Assert.Equal(new long[] { 0, 2 }, offsets);
    }

    [Fact]
    public async Task Persistently_truncated_range_has_a_bounded_retry_count()
    {
        int requests = 0;
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([]) };
        }));
        await using var stream = new HttpRangeReadStream(client, new Uri("http://node/cluster/files/id"), 5, 5, TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<IOException>(async () => await stream.ReadAsync(new byte[5]));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task Empty_read_does_not_start_a_transfer()
    {
        using var client = new HttpClient(new DelegateHandler(_ => throw new InvalidOperationException("unexpected request")));
        await using var stream = new HttpRangeReadStream(client, new Uri("http://node/cluster/files/id"), 5, 5, TimeSpan.FromSeconds(1));
        Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty));
    }

    [Fact]
    public async Task Per_chunk_timeout_applies_while_reading_response_body()
    {
        var content = new TrackingContent(new BlockingReadStream());
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = content
            }));
        var sut = new HttpRangeReadStream(
            client,
            new Uri("http://node/cluster/files/id"),
            length: 1,
            chunkSizeBytes: 1,
            perChunkTimeout: TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await sut.ReadAsync(new byte[1]));
        await sut.DisposeAsync();

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Completed_chunks_dispose_all_responses()
    {
        var contents = new List<TrackingContent>();
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            var content = new TrackingContent(new MemoryStream([42], writable: false));
            contents.Add(content);
            return new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = content
            };
        }));
        var sut = new HttpRangeReadStream(
            client,
            new Uri("http://node/cluster/files/id"),
            length: 2,
            chunkSizeBytes: 1,
            perChunkTimeout: TimeSpan.FromSeconds(1));
        var buffer = new byte[1];

        Assert.Equal(1, await sut.ReadAsync(buffer));
        Assert.Equal(1, await sut.ReadAsync(buffer));
        await sut.DisposeAsync();

        Assert.Equal(2, contents.Count);
        Assert.All(contents, content => Assert.True(content.Disposed));
    }

    [Fact]
    public async Task Unexpected_status_disposes_response()
    {
        var content = new TrackingContent(new MemoryStream());
        using var client = new HttpClient(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            }));
        await using var sut = new HttpRangeReadStream(
            client,
            new Uri("http://node/cluster/files/id"),
            length: 1,
            chunkSizeBytes: 1,
            perChunkTimeout: TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<IOException>(
            async () => await sut.ReadAsync(new byte[1]));

        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Too_many_redirects_dispose_every_response()
    {
        var contents = new List<TrackingContent>();
        using var client = new HttpClient(new DelegateHandler(request =>
        {
            var content = new TrackingContent(new MemoryStream());
            contents.Add(content);
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
            {
                Content = content
            };
            response.Headers.Location = new Uri(request.RequestUri!, "/next");
            return response;
        }));
        var request = new HttpRequestMessage(HttpMethod.Get, "http://node/start");

        await Assert.ThrowsAsync<IOException>(
            () => HttpRedirect.SendWithRedirectAsync(client, request, CancellationToken.None));

        Assert.Equal(5, contents.Count);
        Assert.All(contents, content => Assert.True(content.Disposed));
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class TrackingContent(Stream stream) : HttpContent
    {
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) =>
            stream.CopyToAsync(target);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult(stream);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = stream.CanSeek ? stream.Length : 0;
            return stream.CanSeek;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
