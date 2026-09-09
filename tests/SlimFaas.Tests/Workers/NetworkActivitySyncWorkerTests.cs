using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Workers;

public sealed class NetworkActivitySyncWorkerTests
{
    private static NetworkActivityEvent Event(string id) => new(id, "request_in", "external", "slimfaas", null, 1000, "peer");
    private static HttpResponseMessage Reply(long watermark, string instance, params NetworkActivityEvent[] events)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(events.ToList(), StatusStreamSerializerContext.Default.ListNetworkActivityEvent)) };
        response.Headers.Add("X-Activity-Watermark", watermark.ToString());
        response.Headers.Add("X-Activity-Instance", instance);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request);
    }
    private static NetworkActivitySyncWorker Worker(NetworkActivityTracker tracker, HttpClient client, int peers = 1)
    {
        var replicas = new Mock<IReplicasService>();
        var pods = Enumerable.Range(0, peers).Select(i => new PodInformation($"peer-{i}", true, true, $"10.0.0.{i + 1}", "slimfaas", [5000])).ToList();
        pods.Add(new PodInformation(tracker.NodeId, true, true, "10.0.0.250", "slimfaas", [5000]));
        replicas.SetupGet(r => r.Deployments).Returns(new DeploymentsInformations([], new SlimFaasDeploymentInformation(peers + 1, pods), []));
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(f => f.CreateClient("ActivitySync")).Returns(client);
        var ns = new Mock<INamespaceProvider>(); ns.SetupGet(n => n.CurrentNamespace).Returns("default");
        return new(replicas.Object, tracker, factory.Object, Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { BaseFunctionPodUrl = "http://{pod_ip}:{pod_port}" }), ns.Object, NullLogger<NetworkActivitySyncWorker>.Instance);
    }

    [Fact]
    public async Task Metadata_and_idle_sessions_do_not_scrape_and_a_new_activity_session_bootstraps_again()
    {
        int calls = 0;
        using var client = new HttpClient(new Handler(request => { calls++; Assert.Contains("windowMs=", request.RequestUri!.Query); return Task.FromResult(Reply(1000, "process-a")); }));
        var tracker = new NetworkActivityTracker(); using var worker = Worker(tracker, client);
        Assert.True(tracker.TryReserveStreamClient());
        await worker.ScrapeAllPeersAsync(default); Assert.Equal(0, calls);
        tracker.ReleaseStreamClient();
        var (_, channel) = tracker.Subscribe(); long firstSession = tracker.LiveSessionStartedAt;
        await worker.ScrapeAllPeersAsync(default); Assert.Equal(1, calls);
        tracker.Unsubscribe(channel);
        await worker.ScrapeAllPeersAsync(default); Assert.Equal(1, calls); Assert.Equal(0, tracker.LiveSessionStartedAt);
        var (_, next) = tracker.Subscribe(); Assert.NotEqual(firstSession, tracker.LiveSessionStartedAt);
        await worker.ScrapeAllPeersAsync(default); Assert.Equal(2, calls);
        tracker.Unsubscribe(next);
    }

    [Fact]
    public async Task Timestamp_overlap_keeps_events_added_in_the_same_millisecond_without_duplicates()
    {
        var tracker = new NetworkActivityTracker(); var (_, channel) = tracker.Subscribe(); int calls = 0;
        using var client = new HttpClient(new Handler(request => {
            calls++;
            if (calls == 1) { Assert.Contains("windowMs=", request.RequestUri!.Query); return Task.FromResult(Reply(1000, "a", Event("first"))); }
            Assert.Equal("?since=999", request.RequestUri!.Query);
            return Task.FromResult(Reply(1000, "a", Event("first"), Event("same-ms")));
        }));
        using var worker = Worker(tracker, client);
        await worker.ScrapeAllPeersAsync(default); await worker.ScrapeAllPeersAsync(default); await worker.ScrapeAllPeersAsync(default);
        Assert.Equal(new[] { "first", "same-ms" }, tracker.GetRecent().Select(e => e.Id));
        tracker.Unsubscribe(channel);
    }

    [Fact]
    public async Task Restarted_peer_uses_a_fresh_window_even_if_its_clock_moved_backwards()
    {
        var tracker = new NetworkActivityTracker(); var (_, channel) = tracker.Subscribe(); int calls = 0;
        using var client = new HttpClient(new Handler(request => {
            calls++;
            if (calls == 1) return Task.FromResult(Reply(1000, "old", Event("old-1")));
            if (calls == 2) return Task.FromResult(Reply(500, "new"));
            Assert.Contains("windowMs=", request.RequestUri!.Query);
            return Task.FromResult(Reply(500, "new", Event("new-1") with { TimestampMs = 500 }));
        }));
        using var worker = Worker(tracker, client);
        await worker.ScrapeAllPeersAsync(default); await worker.ScrapeAllPeersAsync(default);
        Assert.Equal(3, calls); Assert.Equal(new[] { "old-1", "new-1" }, tracker.GetRecent().Select(e => e.Id));
        tracker.Unsubscribe(channel);
    }

    [Fact]
    public async Task Older_peer_primes_its_cursor_without_replaying_its_history()
    {
        var tracker = new NetworkActivityTracker(); var (_, channel) = tracker.Subscribe();
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new List<NetworkActivityEvent> { Event("old") }, StatusStreamSerializerContext.Default.ListNetworkActivityEvent)) })));
        using var worker = Worker(tracker, client);
        await worker.ScrapeAllPeersAsync(default); Assert.Empty(tracker.GetRecent());
        tracker.Unsubscribe(channel);
    }

    [Fact]
    public async Task At_most_four_peer_requests_run_concurrently_and_disconnect_discards_inflight_results()
    {
        var tracker = new NetworkActivityTracker(); var (_, channel) = tracker.Subscribe();
        int active = 0, total = 0;
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async _ => {
            Interlocked.Increment(ref total);
            int current = Interlocked.Increment(ref active); Assert.InRange(current, 1, 4);
            if (current == 4) full.TrySetResult();
            await release.Task; Interlocked.Decrement(ref active);
            return Reply(1000, "a", Event("discard"));
        }));
        using var worker = Worker(tracker, client, 12);
        var scrape = worker.ScrapeAllPeersAsync(default);
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(4, total);
        tracker.Unsubscribe(channel); release.SetResult(); await scrape;
        Assert.Equal(4, total); Assert.Empty(tracker.GetRecent());
    }

    [Theory]
    [InlineData("http://{pod_ip}:{pod_port_0}", 3363, 31022, 31022)]
    [InlineData("http://{pod_ip}:3262", 3262, 5000, 5000)]
    [InlineData("http://{pod_ip}:3262", 5000, 3262, 5000)]
    public void Peer_activity_uses_the_application_port_regardless_of_raft_port_order(string raftUrl, int first, int second, int expected)
    {
        var pod = new PodInformation("peer", true, true, "127.0.0.1", "slimfaas", [first, second]);
        var options = new SlimFaasOptions { BaseSlimDataUrl = raftUrl, BaseFunctionPodUrl = "http://{pod_ip}:{pod_port}" };
        Assert.Equal(expected, NetworkActivitySyncWorker.ActivityEndpoint(pod, options, "default")!.Port);
    }

    [Fact]
    public void Explicit_peer_application_port_and_indexed_templates_keep_their_original_meaning()
    {
        var pod = new PodInformation("peer", true, true, "127.0.0.1", "slimfaas", [3262, 5000]);
        foreach (string template in new[] { "http://{pod_ip}:5000", "http://{pod_ip}:{pod_port_1}" })
        {
            var options = new SlimFaasOptions { BaseFunctionPodUrl = template };
            Assert.Equal(5000, NetworkActivitySyncWorker.ActivityEndpoint(pod, options, "default")!.Port);
        }
    }

    [Fact]
    public void Process_restarts_cannot_reuse_event_ids()
    {
        var before = new NetworkActivityTracker(); var after = new NetworkActivityTracker();
        Assert.Equal(before.NodeId, after.NodeId);
        Assert.NotEqual(before.Record("request_in", "external", "slimfaas"), after.Record("request_in", "external", "slimfaas"));
    }
}
