using System.Net;
using System.Text.Json;
using SlimFaas.Benchmarking;

namespace SlimFaas.Tests.Benchmark;

public sealed class RequestJournalTests
{
    [Fact]
    public async Task Concurrent_requests_keep_intents_ids_hashes_and_response_destinations_in_complete_lines()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new RequestJournal(directory);
            using var transport = new ObservedTransport();
            using HttpMessageHandler observed = journal.Wrap(transport);
            using var client = new HttpClient(observed, disposeHandler: false);
            await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "http://function/echo")
                {
                    Content = new ByteArrayContent([42])
                };
                request.Headers.Add("X-Chaos-Id", "operation-" + index);
                using HttpResponseMessage response = await client.SendAsync(request);
                response.EnsureSuccessStatusCode();
            }));

            string[] lines = await ReadLiveJournalAsync(journal.Path);
            Assert.Equal(128, lines.Length);
            var intents = new HashSet<string>(StringComparer.Ordinal);
            var responses = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement value = document.RootElement;
                Assert.Equal(1, value.GetProperty("BodyBytes").GetInt64());
                Assert.Equal("684888c0ebb17f374298b65ee2807526c066094c701bcc7ebbe1c1095f494fc1", value.GetProperty("Sha256").GetString());
                string id = value.GetProperty("Id").GetString()!;
                if (value.GetProperty("Stage").GetString() == "intent")
                    Assert.True(intents.Add(id));
                else
                {
                    Assert.True(responses.Add(id));
                    Assert.Equal("pod-uid", value.GetProperty("ServingPodUid").GetString());
                    Assert.Equal("2.0", value.GetProperty("HttpVersion").GetString());
                    Assert.Equal("queue-element-id", value.GetProperty("QueueElementId").GetString());
                }
            }
            Assert.True(intents.SetEquals(responses));
            using var restarted = new RequestJournal(directory);
            Assert.NotEqual(journal.Path, restarted.Path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_transport_failure_preserves_its_intent_and_an_unknown_outcome_without_retrying()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new RequestJournal(directory);
            using var transport = new ObservedTransport { Fail = true };
            using HttpMessageHandler observed = journal.Wrap(transport);
            using var client = new HttpClient(observed, disposeHandler: false);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://function/echo"));
            string[] lines = await ReadLiveJournalAsync(journal.Path);
            Assert.Equal(1, transport.Calls);
            Assert.Equal(2, lines.Length);
            using JsonDocument first = JsonDocument.Parse(lines[0]);
            using JsonDocument second = JsonDocument.Parse(lines[1]);
            Assert.Equal("intent", first.RootElement.GetProperty("Stage").GetString());
            Assert.Equal("unknown", second.RootElement.GetProperty("Stage").GetString());
            Assert.Equal(first.RootElement.GetProperty("Id").GetString(), second.RootElement.GetProperty("Id").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_unwritable_intent_journal_prevents_transmission()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            using var journal = new RequestJournal(directory);
            using var transport = new ObservedTransport();
            using HttpMessageHandler observed = journal.Wrap(transport);
            using var client = new HttpClient(observed, disposeHandler: false);
            journal.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetAsync("http://function/echo"));
            Assert.Equal(0, transport.Calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string[]> ReadLiveJournalAsync(string path)
    {
        // On Windows, the reader must share writes with the already-open journal.
        // The writer still exclusively owns writes and flushes each complete line.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private sealed class ObservedTransport : HttpMessageHandler
    {
        private int _calls;
        internal bool Fail { get; init; }
        internal int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Assert.Single(request.Headers.GetValues("X-Chaos-Id"));
            if (Fail)
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Response may have been lost"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20 };
            response.Headers.Add("X-Chaos-Pod-Uid", "pod-uid");
            response.Headers.Add("SlimFaas-Element-Id", "queue-element-id");
            return Task.FromResult(response);
        }
    }
}
