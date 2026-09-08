using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using DotNext;
using MemoryPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Security;

namespace SlimFaas.Tests.Endpoints;

public class DataStatusStreamTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class State : ISupplier<SlimDataPayload>
    {
        public int Reads { get; private set; }
        public SlimDataPayload Payload = new()
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        };
        public SlimDataPayload Invoke() { Reads++; return Payload; }
        public void Set(string key, byte[] value) => Payload.KeyValues = Payload.KeyValues.SetItem(key, value);
        public void Expire(string key, DateTimeOffset expiry) => Set(key + SlimDataInterpreter.TimeToLivePostfix, BitConverter.GetBytes(expiry.UtcTicks));
    }

    private static DataStatusSnapshotCache Cache(State state, Clock clock) => new(state, Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions()), clock);

    [Fact]
    public void InventoryNeverSerializesValuesAndExcludesTechnicalExpiredAndInternalEntries()
    {
        var state = new State(); var clock = new Clock();
        state.Set("data:set:visible", Encoding.UTF8.GetBytes("secret-value-must-not-leak"));
        state.Set("data:set:expired", []); state.Expire("data:set:expired", clock.Now);
        state.Set("data:set:soon", []); state.Expire("data:set:soon", clock.Now.AddSeconds(30));
        state.Set("private-internal-key", []);
        state.Expire("data:set:orphan", clock.Now.AddMinutes(1));
        state.Set(DataFileKeys.MetaKey(DataFileKeys.CreateInternalOffloadId()), []);
        var result = Cache(state, clock).GetPage("sets", "", "", 100);
        Assert.Equal(["soon", "visible"], result.Entries.Select(e => e.Id));
        Assert.Null(result.Entries[1].ExpiresAtMs);
        Assert.Equal(clock.Now.AddSeconds(30).ToUnixTimeMilliseconds(), result.Entries[0].ExpiresAtMs);
        Assert.Equal(1, result.Summary.ExpiringSoon);
        var json = JsonSerializer.Serialize(result, StatusStreamSerializerContext.Default.DataStatusPage);
        Assert.DoesNotContain("secret-value", json);
        Assert.DoesNotContain("private-internal", json);
        Assert.Equal(0, result.Summary.Files);
    }

    [Fact]
    public void FileSizesComeFromMetadataAndCorruptMetadataRemainsUnknown()
    {
        var state = new State(); var clock = new Clock();
        state.Set(DataFileKeys.MetaKey("report"), MemoryPackSerializer.Serialize(new DataSetMetadata("secret-hash", 123456, "secret-type", "secret-filename")));
        state.Set(DataFileKeys.MetaKey("broken"), [0xFA]);
        state.Set(DataFileKeys.MetaKey("expired"), []); state.Expire(DataFileKeys.MetaKey("expired"), clock.Now.AddSeconds(-1));
        var result = Cache(state, clock).GetPage("files", "", "", 100);
        Assert.Equal(["broken", "report"], result.Entries.Select(e => e.Id));
        Assert.Null(result.Entries[0].SizeBytes);
        Assert.Equal(123456, result.Entries[1].SizeBytes);
        Assert.Equal(123456, result.Summary.FileBytes);
        Assert.Equal(1, result.Summary.UnknownFileSizes);
        Assert.DoesNotContain("secret-", JsonSerializer.Serialize(result, StatusStreamSerializerContext.Default.DataStatusPage));
    }

    [Fact]
    public void SnapshotIsSharedAndReflectsChangesAndExpiryOnNextInterval()
    {
        var state = new State(); var clock = new Clock();
        state.Set("data:set:short", []); state.Expire("data:set:short", clock.Now.AddMilliseconds(500));
        var cache = Cache(state, clock);
        Assert.Single(cache.GetPage("sets", "", "", 100).Entries);
        state.Set("data:set:new", []);
        _ = cache.GetPage("files", "", "", 100);
        Assert.Equal(1, state.Reads);
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal("new", Assert.Single(cache.GetPage("sets", "", "", 100).Entries).Id);
        Assert.Equal(2, state.Reads);
        state.Expire("data:set:new", clock.Now.AddMinutes(5));
        clock.Now = clock.Now.AddSeconds(1);
        Assert.NotNull(Assert.Single(cache.GetPage("sets", "", "", 100).Entries).ExpiresAtMs);
    }

    [Fact]
    public void PagesAreOrderedFilteredAndBoundedEvenWhenCursorDisappears()
    {
        var state = new State(); var clock = new Clock();
        for (var i = 0; i < 1005; i++) state.Set($"data:set:key-{i:D4}", []);
        state.Set("data:set:other", []);
        var cache = Cache(state, clock);
        var first = cache.GetPage("sets", "key-", "", 100);
        Assert.Equal(100, first.Entries.Count); Assert.Equal(1005, first.TotalCount);
        Assert.Equal("key-0099", first.NextCursor);
        state.Payload.KeyValues = state.Payload.KeyValues.Remove("data:set:key-0099");
        clock.Now = clock.Now.AddSeconds(1);
        var next = cache.GetPage("sets", "key-", first.NextCursor!, 100);
        Assert.Equal("key-0100", next.Entries[0].Id);
        Assert.Empty(cache.GetPage("sets", "missing-", "", 100).Entries);
        Assert.Empty(cache.GetPage("sets", "key-", "zzz", 100).Entries);
        Assert.Null(cache.GetPage("sets", "key-", "key-1003", 100).NextCursor);
    }

    private static async Task<IHost> Host(bool expose = false, bool isPublic = false, bool isInternal = false, bool front = true, int maxClients = 1)
    {
        var access = new Mock<IFunctionAccessPolicy>();
        access.Setup(p => p.IsInternalRequest(It.IsAny<HttpContext>())).Returns(isInternal);
        var state = new State(); state.Set("data:set:test", Encoding.UTF8.GetBytes("private"));
        return await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddSingleton<ISupplier<SlimDataPayload>>(state);
            services.AddSingleton(access.Object);
            services.AddSingleton(new Mock<IDatabaseService>().Object);
            services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
            services.Configure<SlimFaasOptions>(o => { o.ExposeDataMetadata = expose; o.EnableFront = front; o.StatusStream.MaxSseClients = maxClients; });
            services.Configure<DataOptions>(o => o.DefaultVisibility = isPublic ? FunctionVisibility.Public : FunctionVisibility.Private);
            services.AddSingleton<DataStatusSnapshotCache>(); services.AddSingleton<NetworkActivityTracker>();
        }).Configure(app => { app.UseRouting(); app.UseEndpoints(e => { e.MapDataStatusStreamEndpoints(); e.MapDataSetRoutes(); }); })).StartAsync();
    }

    [Theory]
    [InlineData(false, false, false, true, 404)]
    [InlineData(true, false, false, true, 200)]
    [InlineData(false, true, false, true, 200)]
    [InlineData(false, false, true, true, 200)]
    [InlineData(true, true, true, false, 404)]
    public async Task AccessRespectsMetadataOptionAndExistingDataPolicy(bool expose, bool isPublic, bool isInternal, bool front, int expected)
    {
        using var host = await Host(expose, isPublic, isInternal, front);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-data-stream"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(expected, (int)response.StatusCode);
        if (expected == 200)
        {
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
            Assert.Equal("event: data_state", await reader.ReadLineAsync(cts.Token));
            var data = await reader.ReadLineAsync(cts.Token);
            Assert.Contains("test", data); Assert.DoesNotContain("private", data);
        }
        await cts.CancelAsync();
    }

    [Theory]
    [InlineData("kind=hashsets")]
    [InlineData("limit=0")]
    [InlineData("limit=501")]
    [InlineData("limit=wrong")]
    public async Task InvalidQueriesAreRejected(string query)
    {
        using var host = await Host(expose: true);
        using var response = await host.GetTestClient().GetAsync($"http://localhost:5000/status-data-stream?{query}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ActivityAndInventoryShareClientLimitAndDisconnectionReleasesSlot()
    {
        using var host = await Host(expose: true);
        var tracker = host.Services.GetRequiredService<NetworkActivityTracker>();
        Assert.True(tracker.TrySubscribe(out _, out var channel));
        using var denied = await host.GetTestClient().GetAsync("http://localhost:5000/status-data-stream");
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        tracker.Unsubscribe(channel);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var response = await host.GetTestClient().SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/status-data-stream"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.False(tracker.TrySubscribe(out _, out _));
        await cts.CancelAsync();
        response.Dispose();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!tracker.TryReserveStreamClient()) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        tracker.ReleaseStreamClient();
    }

    [Fact]
    public async Task ExposingMetadataDoesNotGrantAccessToPrivateValues()
    {
        using var host = await Host(expose: true);
        using var response = await host.GetTestClient().GetAsync("http://localhost:5000/data/sets/test");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WrongHostPortDoesNotExposeMetadata()
    {
        using var host = await Host(expose: true);
        using var response = await host.GetTestClient().GetAsync("http://localhost:9999/status-data-stream");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
