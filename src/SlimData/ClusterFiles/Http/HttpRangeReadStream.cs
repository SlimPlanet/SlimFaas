using System.Net;
using System.Net.Http.Headers;

namespace SlimData.ClusterFiles.Http;

/// <summary>
/// Stream lecture séquentielle qui télécharge en chunks (Range bytes=...),
/// pour éviter une requête unique géante.
/// </summary>  
internal sealed class HttpRangeReadStream : Stream
{
    private readonly HttpClient _http;
    private readonly Uri _uri;
    private readonly int _chunkSize;
    private readonly TimeSpan _perChunkTimeout;

    private readonly long _length;
    private long _position;

    private HttpResponseMessage? _resp;
    private Stream? _respStream;
    private long _remainingInChunk;

    public HttpRangeReadStream(HttpClient http, Uri uri, long length, int chunkSizeBytes, TimeSpan perChunkTimeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        _length = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));
        _chunkSize = chunkSizeBytes > 0 ? chunkSizeBytes : throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes));
        _perChunkTimeout = perChunkTimeout > TimeSpan.Zero ? perChunkTimeout : throw new ArgumentOutOfRangeException(nameof(perChunkTimeout));
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= _length)
            return 0;

        await EnsureChunkAsync(cancellationToken).ConfigureAwait(false);

        var toRead = (int)Math.Min(buffer.Length, _remainingInChunk);
        var n = await _respStream!.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);

        if (n <= 0)
            throw new IOException("Remote stream ended unexpectedly during ranged download.");

        _position += n;
        _remainingInChunk -= n;
        return n;
    }

    private async Task EnsureChunkAsync(CancellationToken ct)
    {
        if (_respStream is not null && _remainingInChunk > 0)
            return;

        await DisposeCurrentResponseAsync().ConfigureAwait(false);

        var offset = _position;
        var remaining = _length - offset;
        var take = Math.Min((long)_chunkSize, remaining);
        if (take <= 0) return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_perChunkTimeout);

        var req = new HttpRequestMessage(HttpMethod.Get, _uri);
        req.Headers.Range = new RangeHeaderValue(offset, offset + take - 1);

        _resp = await HttpRedirect.SendWithRedirectAsync(_http, req, cts.Token).ConfigureAwait(false);

        if (_resp.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("Remote did not have the file.");

        if (_resp.StatusCode != HttpStatusCode.PartialContent)
            throw new IOException($"Unexpected status for Range GET: {(int)_resp.StatusCode} {_resp.ReasonPhrase}");

        _respStream = await _resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        _remainingInChunk = take;
    }

    private async ValueTask DisposeCurrentResponseAsync()
    {
        try
        {
            if (_respStream is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
            else _respStream?.Dispose();
        }
        catch { /* ignore */ }
        finally
        {
            _respStream = null;
            _remainingInChunk = 0;
            try { _resp?.Dispose(); } catch { /* ignore */ }
            _resp = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            DisposeCurrentResponseAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeCurrentResponseAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// Follow redirects 301/302/307/308 en conservant les headers (Range compris).
/// HttpClient AllowAutoRedirect doit être false.
/// </summary>
internal static class HttpRedirect
{
    public static async Task<HttpResponseMessage> SendWithRedirectAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        Uri current = req.RequestUri!;
        for (int i = 0; i < 5; i++)
        {
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (resp.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                or HttpStatusCode.Redirect or HttpStatusCode.Moved or HttpStatusCode.Found)
            {
                var loc = resp.Headers.Location;
                if (loc is null)
                    return resp;

                resp.Dispose();
                current = loc.IsAbsoluteUri ? loc : new Uri(current, loc);

                var newReq = new HttpRequestMessage(req.Method, current);

                // copy headers (Range, etc.)
                foreach (var h in req.Headers)
                    newReq.Headers.TryAddWithoutValidation(h.Key, h.Value);

                // copy content headers if any (rare here)
                if (req.Content is not null)
                    newReq.Content = req.Content;

                req.Dispose();
                req = newReq;
                continue;
            }

            return resp;
        }

        throw new IOException("Too many redirects while contacting cluster node.");
    }
}
