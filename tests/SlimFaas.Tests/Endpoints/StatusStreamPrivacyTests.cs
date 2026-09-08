using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public class StatusStreamPrivacyTests
{
    private static NetworkActivityEvent Event(string? source, string? target) =>
        new("event-1", "request_out", "external", "fibonacci", null, 1, "slimfaas-0", source, target, "correlation-1");

    [Theory]
    [InlineData("10.42.0.5", "::ffff:10.42.0.5")]
    [InlineData("2001:db8::5", "2001:0db8:0000:0000:0000:0000:0000:0005")]
    [InlineData("fe80::5%2", "fe80:0000:0000:0000:0000:0000:0000:0005%2")]
    public void EquivalentAddressesHaveTheSameOpaqueToken(string first, string equivalent)
    {
        var original = Event(first, equivalent);
        var projected = StatusStreamPrivacy.ForBrowser(original);
        Assert.Matches("^ip_[0-9a-f]{64}$", projected.SourcePod!);
        Assert.Equal(projected.SourcePod, projected.TargetPod);
        Assert.Equal(projected, StatusStreamPrivacy.ForBrowser(original));
        Assert.False(IPAddress.TryParse(projected.SourcePod, out _));
        Assert.Equal(original, projected with { SourcePod = first, TargetPod = equivalent });
        Assert.Equal(first, original.SourcePod);
        Assert.NotEqual(projected.SourcePod, StatusStreamPrivacy.ForBrowser(Event("10.42.0.6", null)).SourcePod);
    }

    [Fact]
    public void ScopedIpv6AddressesRemainDistinct()
    {
        var projected = StatusStreamPrivacy.ForBrowser(Event("fe80::5%2", "fe80::5%3"));
        Assert.NotEqual(projected.SourcePod, projected.TargetPod);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void LoopbackCallersStayExternalWhileTargetsRemainCorrelatable(string address)
    {
        var projected = StatusStreamPrivacy.ForBrowser(Event(address, address));
        Assert.Null(projected.SourcePod);
        Assert.StartsWith("ip_", projected.TargetPod);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fibonacci-replica-1")]
    [InlineData("daily-report-slimfaas-job-deleted")]
    [InlineData("websocket-connection-abc")]
    public void NonAddressIdentitiesArePreserved(string? identity)
    {
        var original = Event(identity, identity);
        Assert.Equal(original, StatusStreamPrivacy.ForBrowser(original));
    }

    [Theory]
    [InlineData(1, "activity")]
    [InlineData(100, "activity_batch")]
    public async Task PublicFramesHideAddressesAndPreserveInternalEvents(int batchSize, string activityType)
    {
        const string ipv4 = "10.42.0.5";
        const string ipv6 = "2001:db8::5";
        const string job = "daily-report-slimfaas-job-run1";
        var deployments = new DeploymentsInformations(
            [new DeploymentInformation("fibonacci", "default",
                [new("replica-v4", true, true, ipv4, "fibonacci"), new("replica-v6", true, true, ipv6, "fibonacci")],
                new SlimFaasConfiguration(), Replicas: 2)],
            new SlimFaasDeploymentInformation(1, [new("slimfaas-0", true, true, "10.0.0.10", "slimfaas")]), []);
        var replicas = new Mock<IReplicasService>();
        replicas.SetupGet(r => r.Deployments).Returns(deployments);
        var jobs = new Mock<IJobService>();
        jobs.SetupGet(j => j.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddMemoryCache();
                services.AddOptions<SlimFaasOptions>().Configure(options =>
                {
                    options.StatusStream.StateIntervalMilliseconds = 50;
                    options.StatusStream.LiveActivityBatchSize = batchSize;
                });
                services.AddSingleton(replicas.Object);
                services.AddSingleton(jobs.Object);
                services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                services.AddSingleton<NetworkActivityTracker>();
                services.AddSingleton<FunctionStatusCache>();
                services.AddSingleton<StatusStreamSnapshotCache>();
                services.AddSingleton<GatedSnapshotCache>();
                services.AddSingleton<IStatusStreamSnapshotCache>(s => s.GetRequiredService<GatedSnapshotCache>());
            }).Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
            })).StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tracker = host.Services.GetRequiredService<NetworkActivityTracker>();
        tracker.Record("request_in", "external", "fibonacci", sourcePod: ipv4, targetPod: ipv6);
        var gate = host.Services.GetRequiredService<GatedSnapshotCache>();
        using var client = host.GetTestClient();
        var pending = client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await gate.Ready.Task.WaitAsync(cts.Token);
        // Queue a deterministic burst before the initial frame is released.
        tracker.Record("request_out", "daily-report", "fibonacci", sourcePod: job, targetPod: ipv4);
        tracker.IngestRemote([Event(ipv6, "::ffff:" + ipv4) with { Id = "remote-event", NodeId = "slimfaas-1" }]);
        gate.Release.SetResult();

        using var response = await pending;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var frames = new List<(string Type, string Json)>();
        var activity = new List<NetworkActivityEvent>();
        while (frames.Count(f => f.Type == "state") < 2 || activity.Count < 2)
        {
            var frame = await ReadFrame(reader, cts.Token);
            frames.Add(frame);
            if (frame.Type == "activity") activity.Add(JsonSerializer.Deserialize(frame.Json, StatusStreamSerializerContext.Default.NetworkActivityEvent)!);
            if (frame.Type == "activity_batch") activity.AddRange(JsonSerializer.Deserialize(frame.Json, StatusStreamSerializerContext.Default.ListNetworkActivityEvent)!);
        }
        Assert.Contains(frames, f => f.Type == activityType);
        foreach (var frame in frames)
        {
            Assert.DoesNotContain(ipv4, frame.Json);
            Assert.DoesNotContain(ipv6, frame.Json);
        }
        using var state = JsonDocument.Parse(frames[0].Json);
        var pods = state.RootElement.GetProperty("Functions")[0].GetProperty("Pods");
        var v4Token = pods[0].GetProperty("Ip").GetString();
        var v6Token = pods[1].GetProperty("Ip").GetString();
        Assert.StartsWith("ip_", v4Token);
        Assert.StartsWith("ip_", v6Token);
        var recent = state.RootElement.GetProperty("RecentActivity")[0];
        Assert.Equal(v4Token, recent.GetProperty("SourcePod").GetString());
        Assert.Equal(v6Token, recent.GetProperty("TargetPod").GetString());
        Assert.Equal(job, activity.Single(e => e.NodeId == tracker.NodeId).SourcePod);
        Assert.Equal(v6Token, activity.Single(e => e.NodeId == "slimfaas-1").SourcePod);
        Assert.All(activity, e => Assert.Equal(v4Token, e.TargetPod));

        // Another subscriber receives the same tokens, including the current history.
        using var reconnect = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"),
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reconnectReader = new StreamReader(await reconnect.Content.ReadAsStreamAsync(cts.Token));
        var reconnectFrame = await ReadFrame(reconnectReader, cts.Token);
        Assert.Contains(v4Token!, reconnectFrame.Json);
        Assert.DoesNotContain(ipv4, reconnectFrame.Json);
        Assert.DoesNotContain(ipv6, reconnectFrame.Json);

        // Neither the deployment cache nor internal peer transport may be modified.
        Assert.Equal(ipv4, deployments.Functions[0].Pods[0].Ip);
        Assert.Equal(ipv4, host.Services.GetRequiredService<FunctionStatusCache>().GetAllDetailed(replicas.Object)[0].Pods[0].Ip);
        Assert.Equal(ipv4, tracker.GetRecent()[0].SourcePod);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "10.0.0.10");
        var internalJson = await client.GetStringAsync("http://localhost:5000/internal/activity-events", cts.Token);
        Assert.Contains(ipv4, internalJson);
        Assert.Contains(ipv6, internalJson);
        Assert.DoesNotContain("remote-event", internalJson);
        await cts.CancelAsync();
    }

    private static async Task<(string Type, string Json)> ReadFrame(StreamReader reader, CancellationToken ct)
    {
        string type = "", json = "";
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 && type.Length > 0) return (type, json);
            if (line.StartsWith("event: ", StringComparison.Ordinal)) type = line[7..];
            if (line.StartsWith("data: ", StringComparison.Ordinal)) json = line[6..];
        }
        throw new EndOfStreamException();
    }

    private sealed class GatedSnapshotCache(StatusStreamSnapshotCache cache) : IStatusStreamSnapshotCache
    {
        private int _calls;
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> GetStateFrameAsync(bool includeRecentActivity, CancellationToken ct)
        {
            var frame = await cache.GetStateFrameAsync(includeRecentActivity, ct);
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Ready.SetResult();
                await Release.Task.WaitAsync(ct);
            }
            return frame;
        }
    }
}
