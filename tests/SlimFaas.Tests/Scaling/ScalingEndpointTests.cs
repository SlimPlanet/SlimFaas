using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Scaling;
using SlimFaas.Security;
using SlimFaas.Tests.Endpoints;

namespace SlimFaas.Tests.Scaling;

public sealed class ScalingEndpointTests
{
    [Fact]
    public async Task PeerEndpointsRecognizeSlimFaasNodesWithoutTrustingForwardedOrPartialAddresses()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73);
        var topology = f.Replicas.Deployments with { SlimFaas = new(1, [new("node", true, true, "127.0.0.1", "slimfaas")]) };
        f.Kubernetes.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>())).ReturnsAsync(topology);
        await f.Replicas.SyncDeploymentsAsync("default"); await f.Replicas.CheckScaleAsync("default");
        using var peerHost = await Host(f, internalRequest: true);
        using var accepted = await peerHost.GetTestClient().GetAsync($"http://localhost:5000/internal/scaling/state?function={f.Name}");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        using var externalHost = await Host(f);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:5000/internal/scaling/state?function={f.Name}");
        request.Headers.Add("X-Forwarded-For", "127.0.0.1");
        using var denied = await externalHost.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var context = new DefaultHttpContext(); context.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.10");
        Assert.False(ScalingEndpoints.IsPeerRequest(context, f.Replicas));
    }

    private static ScalingLeaderClient Client(ScalingTestFixture f, Mock<IMasterService>? master = null,
        Mock<IRaftCluster>? raft = null, IHttpClientFactory? factory = null, IReplicasService? replicas = null)
    {
        master ??= new(); master.SetupGet(m => m.IsMaster).Returns(true);
        var ns = new Mock<INamespaceProvider>(); ns.SetupGet(n => n.CurrentNamespace).Returns("default");
        return new(master.Object, replicas ?? f.Replicas, new StatusLeader((raft ?? new()).Object, f.Options, ns.Object),
            f.Diagnostics, f.Simulation, factory ?? new Mock<IHttpClientFactory>().Object, f.Options, ns.Object);
    }

    private static async Task<IHost> Host(ScalingTestFixture f, bool internalRequest = false, int clients = 2)
    {
        var access = new Mock<IFunctionAccessPolicy>(); access.Setup(a => a.IsInternalRequest(It.IsAny<HttpContext>())).Returns(internalRequest);
        f.Options.Value.StatusStream.MaxSseClients = clients;
        f.Options.Value.StatusStream.StateIntervalMilliseconds = 25;
        var leader = Client(f);
        return await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting(); services.AddSingleton(f.Options); services.AddSingleton(access.Object);
            services.AddSingleton<IReplicasService>(f.Replicas);
            services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
            services.AddSingleton(new Mock<IDatabaseService>().Object);
            services.AddSingleton<NetworkActivityTracker>(); services.AddSingleton(leader);
            services.AddSingleton<IScalingLeaderClient>(leader);
        }).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(internalRequest ? "::ffff:127.0.0.1" : "203.0.113.2");
                await next();
            });
            app.UseRouting(); app.UseEndpoints(e => e.MapScalingEndpoints());
        })).StartAsync();
    }

    [Fact]
    public async Task StreamUsesSharedBudgetWithoutTrafficAndReleasesSlotOnDisconnect()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73); await f.Replicas.CheckScaleAsync("default");
        using var host = await Host(f, clients: 1);
        using var client = host.GetTestClient();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(new(HttpMethod.Get, $"http://localhost:5000/status-scaling-stream?function={f.Name}"),
            HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tracker = host.Services.GetRequiredService<NetworkActivityTracker>(); Assert.False(tracker.HasSubscribers);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel.Token));
        async Task<ScalingState> Read()
        {
            while (await reader.ReadLineAsync(cancel.Token) is { } line)
                if (line.StartsWith("data: ")) return JsonSerializer.Deserialize(line[6..], ScalingJsonContext.Default.ScalingState)!;
            throw new InvalidOperationException("No frame");
        }
        var first = await Read(); Assert.NotEmpty(first.Events); Assert.Equal(8, first.Decision!.RawTarget);
        var second = await Read(); Assert.Empty(second.Events); Assert.Equal(first.Session, second.Session);
        using var limited = await client.GetAsync($"http://localhost:5000/status-scaling-stream?function={f.Name}");
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        f.Diagnostics.SetLeadership(false); f.Diagnostics.SetLeadership(true);
        f.Diagnostics.Record(first.Decision, f.Config);
        ScalingState changed;
        do { changed = await Read(); } while (changed.Session == first.Session);
        Assert.NotEmpty(changed.Events);
        await cancel.CancelAsync(); response.Dispose();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!tracker.TryReserveStreamClient()) await Task.Delay(10, deadline.Token);
        tracker.ReleaseStreamClient(); Assert.False(tracker.HasSubscribers);
    }

    [Theory]
    [InlineData(false, 5000, 404)]
    [InlineData(true, 9999, 404)]
    public async Task FrontAndPortRestrictionsApplyToBothPublicEndpoints(bool front, int port, int expected)
    {
        var f = new ScalingTestFixture(); f.Options.Value.EnableFront = front; await f.SetFunctions();
        using var host = await Host(f); using var client = host.GetTestClient();
        using var get = await client.GetAsync($"http://localhost:{port}/status-scaling-stream?function={f.Name}");
        using var post = await client.PostAsync($"http://localhost:{port}/debug/scaling/simulate",
            JsonContent.Create(new ScalingSimulationRequest(f.Name), ScalingJsonContext.Default.ScalingSimulationRequest));
        Assert.Equal(expected, (int)get.StatusCode); Assert.Equal(expected, (int)post.StatusCode);
    }

    [Fact]
    public async Task SimulationEndpointUsesGeneratedContractAndInternalEndpointsRequireInternalAccess()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73);
        using var host = await Host(f); using var client = host.GetTestClient();
        using var response = await client.PostAsync("http://localhost:5000/debug/scaling/simulate",
            JsonContent.Create(new ScalingSimulationRequest(f.Name, Triggers: [f.Override(20)]), ScalingJsonContext.Default.ScalingSimulationRequest));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync(ScalingJsonContext.Default.ScalingSimulationResponse);
        Assert.Equal(8, result!.Current.RawTarget); Assert.Equal(4, result.Simulated.RawTarget);
        using var denied = await client.GetAsync($"http://localhost:5000/internal/scaling/state?function={f.Name}");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        f.Kubernetes.Verify(k => k.ScaleAsync(It.IsAny<ReplicaRequest>()), Times.Never);
    }

    [Theory]
    [InlineData("{", 400)]
    [InlineData("{}", 400)]
    [InlineData("{\"Function\":\"unknown\"}", 404)]
    public async Task InvalidRequestsReturnStructuredErrors(string body, int code)
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); using var host = await Host(f);
        using var response = await host.GetTestClient().PostAsync("http://localhost:5000/debug/scaling/simulate", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(code, (int)response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync(ScalingJsonContext.Default.ScalingError));
    }

    [Fact]
    public async Task OversizedRequestIsRejectedBeforeSimulation()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); using var host = await Host(f);
        using var response = await host.GetTestClient().PostAsync("http://localhost:5000/debug/scaling/simulate",
            new StringContent(new string(' ', 65537), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }

    [Fact]
    public async Task FollowersResolveConfiguredHttpPortCacheFramesAndRefreshWhenLeaderChanges()
    {
        var f = new ScalingTestFixture(); await f.SetFunctions(); f.Scrape(73); await f.Replicas.CheckScaleAsync("default");
        f.Options.Value.BaseSlimDataUrl = "http://{pod_ip}:{pod_port_0}";
        f.Options.Value.BaseFunctionPodUrl = "http://{pod_ip}:{pod_port}";
        var pods = new List<PodInformation> { new("node-a", true, true, "127.0.0.1", "slimfaas", [3262, 30021]),
            new("node-b", true, true, "127.0.0.1", "slimfaas", [3263, 30022]) };
        var replicas = new Mock<IReplicasService>(); replicas.SetupGet(r => r.Deployments)
            .Returns(f.Replicas.Deployments with { SlimFaas = new(2, pods) });
        var member = new Mock<IRaftClusterMember>(); int raftPort = 3262;
        member.SetupGet(m => m.EndPoint).Returns(() => new UriEndPoint(new Uri($"http://127.0.0.1:{raftPort}")));
        var raft = new Mock<IRaftCluster>(); raft.SetupGet(r => r.Leader).Returns(member.Object);
        var master = new Mock<IMasterService>();
        var requests = new List<Uri>();
        using var handler = new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(f.State() with { Session = "leader-" + raftPort }, ScalingJsonContext.Default.ScalingState) };
        });
        var factory = new Mock<IHttpClientFactory>(); factory.Setup(c => c.CreateClient(ScalingLeaderClient.HttpClientName))
            .Returns(() => new HttpClient(handler, disposeHandler: false));
        var client = Client(f, master, raft, factory.Object, replicas.Object);
        master.SetupGet(m => m.IsMaster).Returns(false);
        var a = await client.GetStateAsync(f.Name, default);
        var cached = await client.GetStateAsync(f.Name, default); Assert.Single(requests); Assert.Equal(a.Session, cached.Session);
        Assert.Equal(30021, requests[0].Port); Assert.Equal("/internal/scaling/state", requests[0].AbsolutePath);
        raftPort = 3263;
        var b = await client.GetStateAsync(f.Name, default); Assert.NotEqual(a.Session, b.Session); Assert.Equal(30022, requests[1].Port);
    }
}
