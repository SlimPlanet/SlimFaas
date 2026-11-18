using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlimFaas.Benchmarking;

internal sealed class RequestJournal : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private readonly string _writerId = Guid.NewGuid().ToString("N");
    private readonly string? _podUid = Environment.GetEnvironmentVariable("POD_UID");
    private long _exchange;

    internal RequestJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"requests-{Environment.ProcessId}-{_writerId}.jsonl");
        _writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    internal string Path { get; }

    internal static RequestJournal? Open(string? directory) =>
        string.IsNullOrWhiteSpace(directory) ? null : new RequestJournal(directory);

    internal HttpMessageHandler Wrap(HttpMessageHandler inner) => new JournalHandler(this, inner);

    internal void RecordOperation(string id, string stage, string? error = null) =>
        Write(id, null, stage, null, null, null, error);

    private void Write(string id, string? exchange, string stage, HttpRequestMessage? request,
        HttpResponseMessage? response, string? sha256, string? error)
    {
        var entry = new RequestJournalEntry(1, _writerId, Environment.ProcessId, _podUid,
            DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), Stopwatch.Frequency,
            id, exchange, stage, request?.Method.Method, request?.RequestUri?.ToString(),
            request?.Content?.Headers.ContentLength, sha256, (int?)response?.StatusCode,
            response?.Version.ToString(), Header(response, "X-Chaos-Pod-Uid"),
            Header(response, "X-Chaos-Connection"), Header(response, "SlimFaas-Element-Id"), error);
        lock (_gate)
            _writer.WriteLine(JsonSerializer.Serialize(entry, RequestJournalJsonContext.Default.RequestJournalEntry));
    }

    private static string? Header(HttpResponseMessage? response, string name) =>
        response?.Headers.TryGetValues(name, out IEnumerable<string>? values) == true ? values.FirstOrDefault() : null;

    public void Dispose()
    {
        lock (_gate)
            _writer.Dispose();
    }

    private sealed class JournalHandler(RequestJournal journal, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string exchange = Interlocked.Increment(ref journal._exchange).ToString(CultureInfo.InvariantCulture);
            string id = request.Headers.TryGetValues("X-Chaos-Id", out IEnumerable<string>? ids)
                ? ids.First()
                : request.Headers.TryGetValues("X-SlimFaas-Benchmark-Request-Id", out ids)
                    ? ids.First() : journal._writerId + "-" + exchange;
            if (!request.Headers.Contains("X-Chaos-Id"))
                request.Headers.TryAddWithoutValidation("X-Chaos-Id", id);
            string? sha256 = request.Headers.TryGetValues("X-Chaos-Sha256", out IEnumerable<string>? hashes)
                ? hashes.First() : null;
            if (cancellationToken.IsCancellationRequested)
            {
                journal.Write(id, exchange, "not-transmitted", request, null, sha256, "Canceled before transport");
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (sha256 is null && request.Content is ByteArrayContent)
            {
                byte[] body = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                sha256 = Convert.ToHexStringLower(SHA256.HashData(body));
                request.Headers.TryAddWithoutValidation("X-Chaos-Sha256", sha256);
            }
            // An intent is flushed before entering the transport. After that point an
            // exception does not establish whether the server executed the request.
            journal.Write(id, exchange, "intent", request, null, sha256, null);
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                journal.Write(id, exchange, "response-headers", request, response, sha256, null);
                HttpResponseMessage result = response;
                response = null; // Ownership passes to the caller only after its observation is journaled.
                return result;
            }
            catch (Exception error)
            {
                journal.Write(id, exchange, "unknown", request, null, sha256, error.GetType().Name + ": " + error.Message);
                throw;
            }
            finally
            {
                response?.Dispose();
            }
        }
    }
}

internal sealed record RequestJournalEntry(int Schema, string Writer, int Pid, string? PodUid,
    DateTimeOffset Utc, long MonotonicTimestamp, long MonotonicFrequency,
    string Id, string? Exchange, string Stage, string? Method, string? Uri, long? BodyBytes,
    string? Sha256, int? Status, string? HttpVersion, string? ServingPodUid, string? Connection,
    string? QueueElementId, string? Error);

[JsonSerializable(typeof(RequestJournalEntry))]
internal sealed partial class RequestJournalJsonContext : JsonSerializerContext;
