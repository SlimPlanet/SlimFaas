using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public sealed class ReactiveStreamTests
{
    private sealed class Snapshot : IStatusStreamSnapshotCache
    {
        public Task<string> GetStateFrameAsync(bool includeRecentActivity, CancellationToken ct) => Task.FromResult("event: state\ndata: {}\n\n");
    }
    private static async Task<IHost> Host(Action<IServiceCollection>? configure = null) => await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
        .ConfigureServices(services => {
            services.AddRouting(); services.AddOptions<SlimFaasOptions>().Configure(o => { o.StatusStream.MaxSseClients = 1; o.Process.Token = "test-token"; });
            var replicas = new Mock<IReplicasService>();
            replicas.SetupGet(r => r.Deployments).Returns(new DeploymentsInformations([], new SlimFaasDeploymentInformation(1, [new("slimfaas", true, true, "10.0.0.10", "slimfaas")]), []));
            services.AddSingleton(replicas.Object);
            var jobs = new Mock<IJobService>(); jobs.SetupGet(j => j.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());
            services.AddSingleton(jobs.Object); services.AddSingleton<NetworkActivityTracker>(); services.AddSingleton<IStatusStreamSnapshotCache, Snapshot>();
            services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
            configure?.Invoke(services);
        }).Configure(app => { app.UseRouting(); app.UseEndpoints(e => { e.MapStatusStreamEndpoints(); e.MapEventEndpoints(); }); })).StartAsync();

    [Fact]
    public async Task State_only_stream_reserves_quota_without_starting_peer_activity()
    {
        using var host = await Host(); using var client = host.GetTestClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream?activity=false"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tracker = host.Services.GetRequiredService<NetworkActivityTracker>();
        Assert.False(tracker.HasSubscribers); Assert.Equal(0, tracker.LiveSessionStartedAt);
        using var denied = await client.GetAsync("http://localhost:5000/status-functions-stream", cts.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Peer_bootstrap_excludes_history_and_returns_an_empty_window_watermark()
    {
        using var host = await Host(); using var client = host.GetTestClient();
        var tracker = host.Services.GetRequiredService<NetworkActivityTracker>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        tracker.IngestRemote([new("historical", "request_in", "external", "slimfaas", null, now - 100000, tracker.NodeId)]);
        tracker.Record("request_in", "external", "slimfaas");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.10");
        using var response = await client.GetAsync("http://localhost:5000/internal/activity-events?windowMs=1000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), StatusStreamSerializerContext.Default.ListNetworkActivityEvent)!;
        Assert.Single(events); Assert.DoesNotContain(events, e => e.Id == "historical");
        Assert.Equal(tracker.InstanceId, response.Headers.GetValues("X-Activity-Instance").Single());
        Assert.True(long.Parse(response.Headers.GetValues("X-Activity-Watermark").Single()) >= now);
        using var empty = await client.GetAsync("http://localhost:5000/internal/activity-events?since=9223372036854775807");
        Assert.Equal("[]", await empty.Content.ReadAsStringAsync()); Assert.True(empty.Headers.Contains("X-Activity-Watermark"));
        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        using var denied = await client.GetAsync("http://localhost:5000/internal/activity-events?windowMs=1000");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Publication_resolves_job_after_access_policy_and_passes_identity_to_websocket_fanout(bool validSignature)
    {
        const string job = "daily-report-slimfaas-job-123";
        var policy = new Mock<IFunctionAccessPolicy>();
        policy.Setup(p => p.GetAllowedSubscribers(It.IsAny<HttpContext>(), "test-event"))
            .Callback<HttpContext, string>((context, _) => Assert.Equal(job, context.Request.Headers[LocalJobGateway.JobHeaderName]))
            .Returns([new DeploymentInformation("ws-function", "websocket-virtual", [], new SlimFaasConfiguration(), Replicas: 1)]);
        var websocket = new Mock<IWebSocketSendClient>();
        websocket.Setup(w => w.PublishEventAsync("ws-function", It.IsAny<CustomRequest>(), "test-event", It.IsAny<CancellationToken>(), It.IsAny<string?>(), activityCorrelationId: It.IsAny<string?>())).Returns(Task.CompletedTask);
        using var host = await Host(services => {
            services.AddSingleton(policy.Object); services.AddSingleton(websocket.Object);
            services.AddSingleton(Mock.Of<ISendClient>()); services.AddSingleton<HistoryHttpMemoryService>(); services.AddSingleton(Mock.Of<INamespaceProvider>());
        });
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add(LocalJobGateway.JobHeaderName, job);
        client.DefaultRequestHeaders.Add(LocalJobGateway.SignatureHeaderName, validSignature ? LocalJobGateway.CreateSignature(job, "test-token") : "invalid");
        using var response = await client.PostAsync("http://localhost:5000/publish-event/test-event", new StringContent("test-payload"));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var activity = host.Services.GetRequiredService<NetworkActivityTracker>().GetRecent();
        Assert.All(activity, e => Assert.Equal(validSignature ? "daily-report" : "external", e.Source));
        if (validSignature) Assert.All(activity, e => Assert.Equal(job, e.SourcePod));
        websocket.Verify(w => w.PublishEventAsync("ws-function", It.Is<CustomRequest>(r => r.Headers.All(h => h.Key != LocalJobGateway.JobHeaderName && h.Key != LocalJobGateway.SignatureHeaderName)), "test-event", It.IsAny<CancellationToken>(), It.Is<string?>(s => validSignature ? s == job : s != job), activityCorrelationId: It.IsAny<string?>()), Times.Once);
    }
}
