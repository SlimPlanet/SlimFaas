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
    [Fact]
    public async Task Catalog_pages_preserve_ordinal_cursors_lookahead_revision_and_single_stream_projection()
    {
        var fixture = new Fixture(true);
        foreach (string name in new[] { "z", "b", "a", "A" }) await fixture.Configure(name);
        var definition = DurableStreamStore.Definition(fixture.Payload, "b")!;
        definition.Revision = 77;
        fixture.State.KeyValues = fixture.State.KeyValues.SetItem(DurableStreamStore.DefinitionPrefix + "b",
            MemoryPack.MemoryPackSerializer.Serialize(definition));
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Append, Stream = "z", Value = [1] }, CancellationToken.None);
        string message = DurableStreamStore.Messages(fixture.Payload,
            DurableStreamStore.Definition(fixture.Payload, "z")!, 0, 1).Single().Id;
        fixture.State.Hashsets = fixture.State.Hashsets.SetItem("\0stream:messages:z",
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add(message, new byte[] { 5 }));
        using var host = await fixture.Start();
        var client = host.GetTestClient();
        using var page = JsonDocument.Parse(await client.GetStringAsync("http://localhost:5000/data/streams?limit=2"));
        Assert.Equal(new[] { "A", "a" }, page.RootElement.GetProperty("Items").EnumerateArray().Select(item => item.GetProperty("Name").GetString()));
        Assert.Equal(77, page.RootElement.GetProperty("Revision").GetInt64());
        string cursor = page.RootElement.GetProperty("NextCursor").GetString()!;
        fixture.State.Hashsets = fixture.State.Hashsets.Remove("\0stream:messages:z");
        // The lookahead row contributes to Revision, as it did before pagination moved into the service.
        using var next = JsonDocument.Parse(await client.GetStringAsync("http://localhost:5000/data/streams?limit=1&after=" + Uri.EscapeDataString(cursor)));
        Assert.Equal("b", next.RootElement.GetProperty("Items")[0].GetProperty("Name").GetString());
        using var metadata = await client.GetAsync("http://localhost:5000/data/streams/a/metadata");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
    }

    [Fact]
    public async Task Function_pages_and_single_function_do_not_decode_off_page_pending_deliveries()
    {
        var fixture = new Fixture(true); await fixture.Configure("orders");
        foreach (string function in new[] { "z", "b", "a" })
            await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Subscribe, Stream = "orders", Function = function }, CancellationToken.None);
        fixture.State.Hashsets = fixture.State.Hashsets.SetItem("\0stream:pending:orders:z",
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty.Add("corrupt", new byte[] { 5 }));
        using var host = await fixture.Start(); var client = host.GetTestClient();
        using var page = JsonDocument.Parse(await client.GetStringAsync("http://localhost:5000/data/streams/orders/functions?limit=1"));
        Assert.Equal("a", page.RootElement.GetProperty("Items")[0].GetProperty("Function").GetString());
        Assert.NotNull(page.RootElement.GetProperty("NextCursor").GetString());
        using var single = await client.GetAsync("http://localhost:5000/data/streams/orders/functions/a");
        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        using var missing = await client.GetAsync("http://localhost:5000/data/streams/orders/functions/missing");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private sealed class Fixture
    {
        internal SlimDataState State { get; } = new(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
        internal SlimDataPayload Payload => new() { KeyValues = State.KeyValues, Hashsets = State.Hashsets, Queues = State.Queues };
        internal IStreamService Service { get; }
        internal SlimDataPayload? ObservationOverride { get; set; }
        internal NetworkActivityTracker Activity { get; } = new();
        internal int BarrierReads { get; private set; }
        internal Func<DurableStreamMutation, Task<DurableStreamMutationResult>>? PersistMutation { get; set; }
        internal Fixture(bool trusted, FunctionVisibility management = FunctionVisibility.Public, bool front = true)
        {
            var database = new Mock<IDatabaseService>();
            database.Setup(item => item.GetAsync(It.IsAny<string>())).Callback(() => BarrierReads++).ReturnsAsync((byte[]?)null);
            database.Setup(item => item.ExecuteStreamAsync(It.IsAny<DurableStreamMutation>()))
                .Returns((DurableStreamMutation mutation) => PersistMutation?.Invoke(mutation) ??
                    Task.FromResult(DurableStreamStore.Apply(mutation, State)));
            var supplier = new Mock<ISupplier<SlimDataPayload>>();
            supplier.Setup(item => item.Invoke()).Returns(() => ObservationOverride ?? new SlimDataPayload
                { KeyValues = State.KeyValues, Hashsets = State.Hashsets, Queues = State.Queues });
            var access = new Mock<IFunctionAccessPolicy>();
            access.Setup(item => item.IsInternalRequest(It.IsAny<HttpContext>())).Returns(trusted);
            var functions = new Mock<IWebSocketFunctionRepository>();
            functions.Setup(item => item.GetVirtualDeployments()).Returns([]);
            Service = new StreamService(database.Object, supplier.Object, new MemoryReplicasService(), functions.Object,
                access.Object, Activity, NullLogger<StreamService>.Instance,
                Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
                    { Streams = new StreamsOptions { DefaultVisibility = management, EnableFront = front } }));
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
    [InlineData("GET", "")]
    [InlineData("GET", "/orders/metadata")]
    [InlineData("GET", "/orders/messages")]
    [InlineData("GET", "/orders/messages/message/body")]
    [InlineData("GET", "/orders/functions")]
    [InlineData("POST", "/orders/functions/worker")]
    [InlineData("POST", "/orders/functions/worker/read")]
    [InlineData("POST", "/orders/functions/worker/ack/message")]
    [InlineData("DELETE", "/orders")]
    [InlineData("DELETE", "/orders/functions/worker")]
    public async Task Private_management_hides_public_streams_without_reading_state(string method, string path)
    {
        var fixture = new Fixture(false, FunctionVisibility.Private);
        await fixture.Configure("orders");
        using var host = await fixture.Start();
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost:5000/data/streams" + path);
        using var response = await host.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, fixture.BarrierReads);
    }

    [Theory]
    [InlineData("/publish-stream/orders")]
    [InlineData("/publish-stream/orders/compute")]
    [InlineData("/data/streams/orders")]
    public async Task Private_management_and_disabled_ui_do_not_disable_public_publication(string route)
    {
        var fixture = new Fixture(false, FunctionVisibility.Private, front: false);
        await fixture.Configure("orders");
        using var host = await fixture.Start();
        using var response = await host.GetTestClient().PostAsync("http://localhost:5000" + route,
            new StringContent("""{"input":10}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("/publish-stream/orders")]
    [InlineData("/publish-stream/orders/compute")]
    [InlineData("/data/streams/orders")]
    public async Task Publication_waits_for_durable_append_but_not_subscriber_delivery(string route)
    {
        var fixture = new Fixture(true);
        await fixture.Configure("orders");
        await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "offline"
        }, CancellationToken.None);
        var entered = new TaskCompletionSource<DurableStreamMutation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource<DurableStreamMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.PersistMutation = mutation =>
        {
            entered.SetResult(mutation);
            return committed.Task;
        };
        using var host = await fixture.Start();
        using var client = host.GetTestClient();
        using var body = new StringContent("""{"orderId":"42"}""", Encoding.UTF8, "application/json");
        var publishing = client.PostAsync("http://localhost:5000" + route, body);
        var append = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DurableStreamAction.Append, append.Action);
        Assert.False(publishing.IsCompleted);
        committed.SetResult(DurableStreamStore.Apply(append, fixture.State));
        using var response = await publishing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("orders", receipt.RootElement.GetProperty("Stream").GetString());
        string id = receipt.RootElement.GetProperty("MessageId").GetString()!;
        Assert.Equal($"/data/streams/orders/messages/{id}", response.Headers.Location?.OriginalString);
        using var retained = await client.GetAsync("http://localhost:5000" + response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        using var subscriptions = JsonDocument.Parse(await client.GetStringAsync("http://localhost:5000/data/streams/orders/functions"));
        var offline = Assert.Single(subscriptions.RootElement.GetProperty("Items").EnumerateArray());
        Assert.Equal(0, offline.GetProperty("AcknowledgedThrough").GetInt64());
        Assert.Equal(1, offline.GetProperty("Lag").GetInt64());
    }

    [Theory]
    [InlineData("/publish-stream/orders")]
    [InlineData("/publish-stream/orders/compute")]
    [InlineData("/data/streams/orders")]
    public async Task First_publication_creates_stream_and_returns_accepted_receipt(string route)
    {
        var fixture = new Fixture(true);
        using var host = await fixture.Start();
        using var response = await host.GetTestClient().PostAsync("http://localhost:5000" + route,
            new StringContent("first"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        using var retained = await host.GetTestClient().GetAsync("http://localhost:5000" + response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
    }

    [Fact]
    public async Task Failed_persistence_does_not_return_an_accepted_receipt()
    {
        var fixture = new Fixture(true);
        await fixture.Configure("orders");
        fixture.PersistMutation = _ => Task.FromException<DurableStreamMutationResult>(
            new InvalidOperationException("Persistence unavailable"));
        using var host = await fixture.Start();
        using var body = new StringContent("not committed");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.GetTestClient().PostAsync("http://localhost:5000/publish-stream/orders", body));
        Assert.Equal("Persistence unavailable", exception.Message);
        var payload = await fixture.Service.ReadAsync(CancellationToken.None);
        Assert.Empty(DurableStreamStore.Messages(payload, DurableStreamStore.Definition(payload, "orders")!, 0, 100));
    }

    [Theory]
    [InlineData(false, FunctionVisibility.Public)]
    [InlineData(true, FunctionVisibility.Private)]
    public async Task Authorized_management_works_with_ui_disabled(bool trusted, FunctionVisibility management)
    {
        var fixture = new Fixture(trusted, management, front: false);
        await fixture.Configure("orders", trusted ? "Private" : "Public");
        using var host = await fixture.Start();
        using var response = await host.GetTestClient().GetAsync("http://localhost:5000/data/streams/orders/metadata");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(fixture.Service.FrontEnabled);
    }

    [Theory]
    [InlineData(false, FunctionVisibility.Public)]
    [InlineData(true, FunctionVisibility.Private)]
    public async Task Unavailable_ui_projects_no_stream_activity_or_observation_reads(bool front, FunctionVisibility management)
    {
        var fixture = new Fixture(false, management, front);
        await fixture.Configure("orders");
        using var host = await fixture.Start();
        var context = new DefaultHttpContext { RequestServices = host.Services };
        var events = new NetworkActivityEvent[]
        {
            new("stream-event", "stream_ack", "worker", "stream:orders", null, 1, "node", StreamName: "orders"),
            new("ordinary", "request_in", "external", "slimfaas", null, 1, "node")
        };
        var original = new StatusStreamPayload([], [], [], events);
        string frame = "event: state\ndata: " +
            JsonSerializer.Serialize(original, StatusStreamSerializerContext.Default.StatusStreamPayload) + "\n\n";
        string projected = await StreamObservation.ProjectAsync(context, frame, CancellationToken.None);
        Assert.DoesNotContain("orders", projected, StringComparison.Ordinal);
        Assert.DoesNotContain("stream-event", projected, StringComparison.Ordinal);
        Assert.Contains("ordinary", projected, StringComparison.Ordinal);
        Assert.Equal(0, fixture.BarrierReads);
    }

    [Fact]
    public void Streams_default_to_private_management_and_disabled_ui()
    {
        var options = new SlimFaasOptions();
        Assert.Equal(FunctionVisibility.Private, options.Streams.DefaultVisibility);
        Assert.False(options.Streams.EnableFront);
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

    [Theory]
    [InlineData(DurableStreamAction.Complete, 200, "stream_ack", "Acknowledged")]
    [InlineData(DurableStreamAction.Complete, 204, "stream_ack", "Acknowledged")]
    [InlineData(DurableStreamAction.Complete, 202, "stream_waiting", "WaitingForCallback")]
    [InlineData(DurableStreamAction.AwaitCallback, 0, "stream_waiting", "WaitingForCallback")]
    public async Task Accepted_completion_activity_does_not_depend_on_a_followers_lagging_snapshot(
        DurableStreamAction action, int status, string eventType, string committedState)
    {
        var fixture = new Fixture(true);
        await fixture.Configure("orders");
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Subscribe, Stream = "orders", Function = "billing" }, CancellationToken.None);
        await fixture.Service.MutateAsync(new() { Action = DurableStreamAction.Append, Stream = "orders", Value = [1] }, CancellationToken.None);
        var reservation = await fixture.Service.MutateAsync(new()
        {
            Action = DurableStreamAction.Reserve, Stream = "orders", Function = "billing",
            Owner = "node1", Replica = "replica1"
        }, CancellationToken.None);
        var observation = new SlimDataPayload
        {
            KeyValues = fixture.State.KeyValues, Hashsets = fixture.State.Hashsets, Queues = fixture.State.Queues
        };
        fixture.ObservationOverride = observation;
        var delivery = Assert.IsType<DurableStreamDelivery>(reservation.Delivery);
        var result = await fixture.Service.MutateAsync(new()
        {
            Action = action, Stream = "orders", Function = "billing",
            ElementId = delivery.ElementId, StatusCode = status
        }, CancellationToken.None);
        Assert.Equal(200, result.Status);
        Assert.Equal("Running", DurableStreamStore.Attempt(observation, delivery.ElementId)!.State);
        var committed = new SlimDataPayload
        {
            KeyValues = fixture.State.KeyValues, Hashsets = fixture.State.Hashsets, Queues = fixture.State.Queues
        };
        Assert.Equal(committedState, DurableStreamStore.Attempt(committed, delivery.ElementId)!.State);
        var observed = Assert.Single(fixture.Activity.GetRecent(), item => item.Type == eventType);
        Assert.Equal("billing", observed.StreamFunction);
        Assert.Equal(delivery.MessageId, observed.StreamMessageId);
        Assert.DoesNotContain(fixture.Activity.GetRecent(), item => item.Type == "stream_failed");
        Assert.Equal(0, fixture.BarrierReads);
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
        Assert.Equal(HttpStatusCode.Accepted, appended.StatusCode);
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
