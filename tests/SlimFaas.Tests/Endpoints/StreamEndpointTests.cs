using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using DotNext;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;
using SlimFaas.Streams;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public sealed class StreamEndpointTests
{
    private sealed class Fixture
    {
        internal SlimDataState State { get; } = new(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
        internal IStreamService Service { get; }
        internal NetworkActivityTracker Activity { get; } = new();
        internal Fixture(bool trusted)
        {
            var database = new Mock<IDatabaseService>();
            database.Setup(item => item.GetAsync(It.IsAny<string>())).ReturnsAsync((byte[]?)null);
            database.Setup(item => item.ExecuteStreamAsync(It.IsAny<DurableStreamMutation>()))
                .ReturnsAsync((DurableStreamMutation mutation) => DurableStreamStore.Apply(mutation, State));
            var supplier = new Mock<ISupplier<SlimDataPayload>>();
            supplier.Setup(item => item.Invoke()).Returns(() => new SlimDataPayload
                { KeyValues = State.KeyValues, Hashsets = State.Hashsets, Queues = State.Queues });
            var access = new Mock<IFunctionAccessPolicy>();
            access.Setup(item => item.IsInternalRequest(It.IsAny<HttpContext>())).Returns(trusted);
            var functions = new Mock<IWebSocketFunctionRepository>();
            functions.Setup(item => item.GetVirtualDeployments()).Returns([]);
            Service = new StreamService(database.Object, supplier.Object, new MemoryReplicasService(), functions.Object,
                access.Object, Activity, NullLogger<StreamService>.Instance);
        }
        internal Task<DurableStreamMutationResult> Configure(string name, string visibility = "Public", int length = 100000) =>
            Service.MutateAsync(new() { Action = DurableStreamAction.Configure, Stream = name, Visibility = visibility, MaxLength = length }, CancellationToken.None);
        internal async Task<IHost> Start() => await new HostBuilder().ConfigureWebHost(builder =>
            builder.UseTestServer().ConfigureServices(services =>
            {
                services.AddRouting(); services.AddSingleton(Service);
                services.Configure<DataOptions>(options => options.DefaultVisibility = FunctionVisibility.Public);
                services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
            }).Configure(app => { app.UseRouting(); app.UseEndpoints(endpoints => endpoints.MapStreamEndpoints()); })).StartAsync();
    }

    [Theory]
    [InlineData("")]
    [InlineData("/metadata")]
    [InlineData("/messages")]
    [InlineData("/messages/missing")]
    [InlineData("/messages/missing/body")]
    [InlineData("/functions")]
    [InlineData("/functions/billing")]
    public async Task Private_streams_are_absent_on_every_observation_surface(string path)
    {
        var fixture = new Fixture(false); await fixture.Configure("private", "Private");
        using var host = await fixture.Start();
        using var response = await host.GetTestClient().GetAsync("http://localhost:5000/data/streams/private" + path);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var catalog = await host.GetTestClient().GetStringAsync("http://localhost:5000/data/streams");
        Assert.DoesNotContain("private", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_function_visibility_uses_active_declarations_not_retired_public_history()
    {
        var fixture = new Fixture(false); await fixture.Configure("old"); await fixture.Configure("current");
        await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "old", Function = "worker", FunctionVisibility = "Public"
        }, CancellationToken.None);
        await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Unsubscribe, Stream = "old", Function = "worker"
        }, CancellationToken.None);
        await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "current", Function = "worker", FunctionVisibility = "Private"
        }, CancellationToken.None);
        Assert.False(fixture.Service.CanObserveFunction(new DefaultHttpContext(), "worker"));
        using var host = await fixture.Start();
        string body = await host.GetTestClient().GetStringAsync("http://localhost:5000/data/streams/current/functions");
        Assert.DoesNotContain("worker", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsubscribe_is_explicit_idempotent_and_protected_by_Stream_visibility()
    {
        var fixture = new Fixture(true); await fixture.Configure("orders");
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing" }, CancellationToken.None);
        using var host = await fixture.Start();
        using var first = await host.GetTestClient().DeleteAsync("http://localhost:5000/data/streams/orders/functions/billing");
        using var second = await host.GetTestClient().DeleteAsync("http://localhost:5000/data/streams/orders/functions/billing");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.False(DurableStreamStore.Subscription(new() { Hashsets = fixture.State.Hashsets }, "orders", "billing")!.Active);
        var restricted = new Fixture(false); await restricted.Configure("private", "Private");
        using var restrictedHost = await restricted.Start();
        using var denied = await restrictedHost.GetTestClient().DeleteAsync("http://localhost:5000/data/streams/private/functions/billing");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task Repeated_callbacks_never_replay_waiting_or_ack_activity()
    {
        var fixture = new Fixture(true);
        await fixture.Configure("orders");
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing" }, CancellationToken.None);
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Append, Stream = "orders", Value = [1] }, CancellationToken.None);
        var reservation = await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Reserve, Stream = "orders", Function = "billing",
            MaximumParallel = 1, MaximumPerReplica = 1, Owner = "node1", Replica = "replica1"
        }, CancellationToken.None);
        foreach (int status in new[] { 202, 202, 200, 200 })
        {
            var result = await fixture.Service.MutateAsync(new()
            {
                Action = DurableStreamAction.Complete, Stream = "orders", Function = "billing",
                ElementId = reservation.Delivery!.ElementId, StatusCode = status
            }, CancellationToken.None);
            Assert.Equal(200, result.Status);
        }
        Assert.Single(fixture.Activity.GetRecent(), item => item.Type == "stream_waiting");
        Assert.Single(fixture.Activity.GetRecent(), item => item.Type == "stream_ack");
    }

    [Fact]
    public async Task Append_alias_preserves_body_and_observation_never_changes_progress()
    {
        var fixture = new Fixture(true);
        await fixture.Configure("orders");
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing" }, CancellationToken.None);
        using var host = await fixture.Start(); var client = host.GetTestClient();
        using var body = new StringContent("<script>untrusted</script>", Encoding.UTF8, "text/html");
        using var appended = await client.PostAsync("http://localhost:5000/publish-stream/orders/compute?x=1", body);
        Assert.Equal(HttpStatusCode.Created, appended.StatusCode);
        using var receipt = JsonDocument.Parse(await appended.Content.ReadAsStringAsync());
        string id = receipt.RootElement.GetProperty("MessageId").GetString()!;
        var before = (fixture.State.KeyValues, fixture.State.Hashsets, fixture.State.Queues);
        using var page = JsonDocument.Parse(await client.GetStringAsync("http://localhost:5000/data/streams/orders?limit=1"));
        Assert.Equal(id, page.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
        Assert.DoesNotContain("<script>", page.RootElement.GetRawText(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:5000/data/streams/orders/messages/{id}/body");
        request.Headers.Range = new(0, 7);
        using var download = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, download.StatusCode);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("<script>", await download.Content.ReadAsStringAsync());
        Assert.Equal(before, (fixture.State.KeyValues, fixture.State.Hashsets, fixture.State.Queues));
    }

    [Theory]
    [InlineData("?limit=0", HttpStatusCode.BadRequest)]
    [InlineData("?limit=501", HttpStatusCode.BadRequest)]
    [InlineData("?after=previous-generation_1", HttpStatusCode.Gone)]
    public async Task History_reports_invalid_limits_and_generation_boundaries(string query, HttpStatusCode expected)
    {
        var fixture = new Fixture(true); await fixture.Configure("orders");
        using var host = await fixture.Start();
        using var response = await host.GetTestClient().GetAsync("http://localhost:5000/data/streams/orders" + query);
        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Private_function_subscriptions_never_enter_public_aggregate_counts()
    {
        var fixture = new Fixture(false); await fixture.Configure("orders");
        await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "secret-worker"
        }, CancellationToken.None);
        using var host = await fixture.Start();
        string root = "http://localhost:5000/data/streams/orders";
        string metadata = await host.GetTestClient().GetStringAsync(root + "/metadata");
        Assert.Contains("\"VisibleFunctionCount\":0", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-worker", await host.GetTestClient().GetStringAsync(root + "/functions"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sse_projection_filters_private_history_and_counts_per_viewer_without_mutating_the_common_frame()
    {
        var fixture = new Fixture(false); await fixture.Configure("secret", "Private"); await fixture.Configure("public");
        var activity = new NetworkActivityEvent("id", "stream_append", "slimfaas", "stream:secret", null, 1, "node",
            StreamName: "secret", StreamVisibility: "Private");
        string frame = "event: state\ndata: " + JsonSerializer.Serialize(new StatusStreamPayload([], [], [], [activity]),
            StatusStreamSerializerContext.Default.StatusStreamPayload) + "\n\n";
        var services = new ServiceCollection().AddSingleton(fixture.Service)
            .Configure<SlimFaasOptions>(options => { options.EnableFront = true; options.ExposeDataMetadata = true; });
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        string projected = await StreamObservation.ProjectAsync(context, frame, CancellationToken.None);
        Assert.DoesNotContain("secret", projected, StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"public\"", projected, StringComparison.Ordinal);
        Assert.Contains("secret", frame, StringComparison.Ordinal);
        Assert.False(fixture.Service.CanObserveActivity(context, activity with { StreamName = "gone" }));
        Assert.True(fixture.Service.CanObserveActivity(context, activity with
            { Type = "stream_deleted", StreamName = "gone", StreamVisibility = "Public", Target = "stream:gone" }));
    }
}
