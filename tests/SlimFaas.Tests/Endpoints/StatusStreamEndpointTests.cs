using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlimFaas.Database;
using SlimFaas.Endpoints;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public class StatusStreamEndpointTests
{
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
                        services.AddSingleton<FunctionStatusCache>();
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
        tracker.Record(NetworkActivityTracker.EventTypes.RequestIn, NetworkActivityTracker.Actors.External, NetworkActivityTracker.Actors.SlimFaas);
        tracker.Record(NetworkActivityTracker.EventTypes.Enqueue, NetworkActivityTracker.Actors.SlimFaas, "fibonacci", "fibonacci");

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
                        services.AddSingleton<FunctionStatusCache>();
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
                        services.AddSingleton<IReplicasService, MemoryReplicasService>();
                        services.AddSingleton<ISlimFaasQueue, MemorySlimFaasQueue>();
                        services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
                        services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
                        services.AddSingleton(tracker);
                        services.AddMemoryCache();
                        services.AddSingleton<FunctionStatusCache>();
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
        var response = await client.GetAsync($"http://localhost:5000/internal/activity-events?since={afterFirstEvent}");

        var json = await response.Content.ReadAsStringAsync();
        var events = JsonSerializer.Deserialize(json, StatusStreamSerializerContext.Default.ListNetworkActivityEvent);

        Assert.NotNull(events);
        Assert.Single(events);
        Assert.Equal("new_event", events[0].Type);
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
                        services.AddSingleton<FunctionStatusCache>();
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
}

