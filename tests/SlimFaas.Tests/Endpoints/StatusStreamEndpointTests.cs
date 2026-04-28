using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimData;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public class StatusStreamEndpointTests
{
    [Fact(DisplayName = "GET /status-functions-stream n'appelle pas SyncDeploymentsAsync")]
    public async Task StatusStream_DoesNotCallSyncDeploymentsAsync()
    {
        var replicasService = new CountingReplicasService();

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService>(replicasService);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

        Assert.Equal(0, replicasService.SyncDeploymentsCallCount);
    }

    [Fact(DisplayName = "GET /status-functions-stream n'appelle pas les sync jobs")]
    public async Task StatusStream_DoesNotCallJobSyncMethods()
    {
        var jobConfigMock = new Mock<IJobConfiguration>();
        var jobServiceMock = new Mock<IJobService>();

        jobConfigMock.SetupGet(c => c.Configuration)
            .Returns(new SlimFaasJobConfiguration(new Dictionary<string, SlimfaasJob>()));
        jobServiceMock.SetupGet(s => s.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton<IJobConfiguration>(jobConfigMock.Object);
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.GetTestClient()
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"), HttpCompletionOption.ResponseHeadersRead, cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);

        jobConfigMock.Verify(c => c.SyncJobsConfigurationAsync(), Times.Never);
        jobServiceMock.Verify(s => s.SyncJobsAsync(), Times.Never);
    }

    [Fact(DisplayName = "GET /status-functions-stream returns text/event-stream")]
    public async Task StatusStream_ReturnsEventStream()
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        // We need to read just the beginning of the stream - use a timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType?.MediaType ?? "");

        // Read first chunk - should contain "event: state"
        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

        Assert.Contains("event: state", text);
        Assert.Contains("Functions", text);
    }

    [Fact(DisplayName = "GET /internal/activity-events returns local events as JSON")]
    public async Task InternalActivityEvents_ReturnsLocalEvents()
    {
        var tracker = new NetworkActivityTracker();
        var replicasService = new InternalAccessReplicasService();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.SetupGet(s => s.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());
        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        tracker.Record(NetworkActivityTracker.EventTypes.Enqueue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", "fibonacci");

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService>(replicasService);
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton(tracker);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", InternalAccessReplicasService.InternalPodIp);
        var response = await client.GetAsync("http://localhost:5000/internal/activity-events?since=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        var events = JsonSerializer.Deserialize(json, StatusStreamSerializerContext.Default.ListNetworkActivityEvent);

        Assert.NotNull(events);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(tracker.NodeId, e.NodeId));
    }

    [Fact(DisplayName = "GET /internal/activity-events with since filters older events")]
    public async Task InternalActivityEvents_FiltersBySince()
    {
        var tracker = new NetworkActivityTracker();
        var replicasService = new InternalAccessReplicasService();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.SetupGet(s => s.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());
        tracker.Record("old_event", NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        var recent = tracker.GetRecent();
        long afterFirstEvent = recent[0].TimestampMs;

        // Small delay to ensure timestamp difference
        await Task.Delay(10);
        tracker.Record("new_event", NetworkActivityTracker.Actors.SlimFaas, "fibonacci");

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService>(replicasService);
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton(tracker);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", InternalAccessReplicasService.InternalPodIp);
        var response = await client.GetAsync($"http://localhost:5000/internal/activity-events?since={afterFirstEvent}");

        var json = await response.Content.ReadAsStringAsync();
        var events = JsonSerializer.Deserialize(json, StatusStreamSerializerContext.Default.ListNetworkActivityEvent);

        Assert.NotNull(events);
        Assert.Single(events);
        Assert.Equal("new_event", events[0].Type);
    }

    [Fact(DisplayName = "GET /internal/activity-events refuse les appels externes")]
    public async Task InternalActivityEvents_ExternalCall_ReturnsForbidden()
    {
        var tracker = new NetworkActivityTracker();
        var jobServiceMock = new Mock<IJobService>();
        jobServiceMock.SetupGet(s => s.Jobs).Returns(new List<SlimFaas.Kubernetes.Job>());

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<IJobService>(jobServiceMock.Object);
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton(tracker);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var response = await host.GetTestClient().GetAsync("http://localhost:5000/internal/activity-events?since=0");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(DisplayName = "SSE stream includes NodeId in events")]
    public async Task StatusStream_EventsContainNodeId()
    {
        var tracker = new NetworkActivityTracker();

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton(tracker);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        // Record an event so it shows up in the initial state
        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        var client = host.GetTestClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[8192];
        var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

        Assert.Contains("NodeId", text);
        Assert.Contains(tracker.NodeId, text);
    }

    [Fact(DisplayName = "SSE stream only includes RecentActivity in initial state")]
    public async Task StatusStream_OnlyInitialStateContainsRecentActivity()
    {
        var tracker = new NetworkActivityTracker();
        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton(tracker);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        var text = "";
        while (CountOccurrences(text, "event: state") < 2)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            Assert.True(read > 0, "The SSE stream ended before sending two state events.");
            text += System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }

        var states = ExtractStatePayloads(text);
        Assert.True(states.Count >= 2);

        using var initialState = JsonDocument.Parse(states[0]);
        using var periodicState = JsonDocument.Parse(states[1]);

        Assert.Equal(1, initialState.RootElement.GetProperty("RecentActivity").GetArrayLength());
        Assert.Equal(0, periodicState.RootElement.GetProperty("RecentActivity").GetArrayLength());
    }

    [Fact(DisplayName = "GET /status-functions-stream returns 429 when MaxSseClients is reached")]
    public async Task StatusStream_ReturnsTooManyRequests_WhenMaxSseClientsReached()
    {
        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddOptions<SlimFaasOptions>().Configure(o => o.StatusStream.MaxSseClients = 1);
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var firstResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        using var secondResponse = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        Assert.Equal((HttpStatusCode)429, secondResponse.StatusCode);
    }

    [Fact(DisplayName = "Status stream reuses queue length cache across state snapshots")]
    public async Task StatusStream_ReusesQueueLengthCache()
    {
        var queue = new CountingSlimFaasQueue();

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IReplicasService, CountingReplicasService>();
                        services.AddSingleton<ISlimFaasQueue>(queue);
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddOptions<SlimFaasOptions>().Configure(o =>
                        {
                            o.StatusStream.StateIntervalMilliseconds = 100;
                            o.StatusStream.QueueLengthsCacheMilliseconds = 10_000;
                        });
                        services.AddMemoryCache();
                        services.AddOptions<SlimFaasOptions>();
                        services.AddSingleton<FunctionStatusCache>();
                        services.AddSingleton<IStatusStreamSnapshotCache, StatusStreamSnapshotCache>();
                        services.AddSingleton<NetworkActivityTracker>();
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints => endpoints.MapStatusStreamEndpoints());
                    });
            })
            .StartAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.GetTestClient().SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-functions-stream"),
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);

        var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[4096];
        var text = "";
        while (CountOccurrences(text, "event: state") < 2)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length, cts.Token);
            Assert.True(read > 0, "The SSE stream ended before sending two state events.");
            text += System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }

        Assert.Equal(1, queue.CountElementCallCount);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static List<string> ExtractStatePayloads(string sseText)
    {
        return sseText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(block => block.StartsWith("event: state\n", StringComparison.Ordinal))
            .Select(block => block.Split('\n').First(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])
            .ToList();
    }

    private sealed class CountingReplicasService : IReplicasService
    {
        public int SyncDeploymentsCallCount;

        public DeploymentsInformations Deployments =>
            new(
                new List<DeploymentInformation>
                {
                    new(Replicas: 0, Deployment: "fibonacci", Namespace: "default",
                        Pods: new List<PodInformation> { new("", true, true, "", "", new List<int>() { 5000 }) }, Configuration: new SlimFaasConfiguration())
                },
                new SlimFaasDeploymentInformation(1, new List<PodInformation> { new("", true, true, "", "", new List<int>() { 5000 }) }),
                new List<PodInformation>());

        public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace)
        {
            Interlocked.Increment(ref SyncDeploymentsCallCount);
            return Task.FromResult(Deployments);
        }

        public Task CheckScaleAsync(string kubeNamespace) => Task.CompletedTask;
    }

    private sealed class InternalAccessReplicasService : IReplicasService
    {
        public const string InternalPodIp = "10.0.0.10";

        public DeploymentsInformations Deployments =>
            new(
                new List<DeploymentInformation>(),
                new SlimFaasDeploymentInformation(2, new List<PodInformation>
                {
                    new("slimfaas-1", true, true, InternalPodIp, "slimfaas", new List<int>() { 5000 }),
                    new("slimfaas-2", true, true, "10.0.0.11", "slimfaas", new List<int>() { 5000 })
                }),
                new List<PodInformation>());

        public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) => Task.FromResult(Deployments);

        public Task CheckScaleAsync(string kubeNamespace) => Task.CompletedTask;
    }

    private sealed class CountingSlimFaasQueue : ISlimFaasQueue
    {
        public int CountElementCallCount;

        public Task<string> EnqueueAsync(string key, byte[] message, RetryInformation retryInformation) => Task.FromResult("id");

        public Task<IList<QueueData>?> DequeueAsync(string key, int count = 1, IList<string>? reservedIps = null) =>
            Task.FromResult<IList<QueueData>?>(Array.Empty<QueueData>());

        public Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => Task.CompletedTask;

        public Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue)
        {
            Interlocked.Increment(ref CountElementCallCount);
            return Task.FromResult(0L);
        }

        public Task<IList<QueueData>> ListElementsAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue) =>
            Task.FromResult<IList<QueueData>>(Array.Empty<QueueData>());
    }
}

