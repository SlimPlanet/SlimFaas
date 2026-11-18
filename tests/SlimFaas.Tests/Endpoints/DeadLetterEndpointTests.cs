using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using MemoryPack;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using SlimData;
using SlimData.Commands;
using SlimFaas.DeadLetters;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.Endpoints;

public sealed class DeadLetterEndpointTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static DeadLetterSnapshot Snapshot()
    {
        var entries = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
        var payloads = entries;
        for (int i = 0; i < 3; i++)
        {
            string id = "dlq-" + i;
            entries = entries.Add(id, MemoryPackSerializer.Serialize(new DeadLetterEntry
            {
                Id = id, Function = "billing", OriginalElementId = "original-" + i, AcceptedAtTicks = Now.UtcTicks - TimeSpan.TicksPerMinute,
                FailedAtTicks = Now.UtcTicks + i, ExpiresAtTicks = Now.UtcTicks + TimeSpan.TicksPerDay,
                Attempts = 1, FailureReason = "NonRetryableResponse", LastHttpStatus = 400 + i
            }));
            payloads = payloads.Add(id, MemoryPackSerializer.Serialize(new CustomRequest(
                [new CustomHeader("Content-Type", ["text/html"]), new CustomHeader("Authorization", ["secret-test"])],
                Encoding.UTF8.GetBytes("<script>not executable</script>"), "billing", "/work", "POST", "?private=test")));
        }
        var payload = new SlimDataPayload
        {
            KeyValues = ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            Queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty,
            Hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty
                .Add(DeadLetterKeys.Messages("billing"), entries).Add(DeadLetterKeys.Payloads("billing"), payloads)
        };
        return new(payload, [new("billing", true, true, 3, Now, Now, 604800, 10000, Now, 3)], Now, 3);
    }

    private static async Task<IHost> Start(Mock<IDeadLetterService> service)
    {
        return await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddSingleton(service.Object);
            services.AddSingleton<ISlimFaasPorts, SlimFaasPortsMock>();
            services.AddSingleton<IReplicasService, MemoryReplicasService>();
            services.AddSingleton<IWebSocketFunctionRepository, WebSocketFunctionRepositoryMock>();
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapDeadLetterEndpoints());
        })).StartAsync();
    }

    private static Mock<IDeadLetterService> Service(bool authorized)
    {
        var service = new Mock<IDeadLetterService>();
        service.Setup(value => value.CanAccess(It.IsAny<Microsoft.AspNetCore.Http.HttpContext>())).Returns(authorized);
        service.Setup(value => value.ReadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Snapshot());
        service.Setup(value => value.OpenBodyAsync(It.IsAny<DeadLetterEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("<script>not executable</script>")));
        return service;
    }

    [Theory]
    [InlineData("GET", "")]
    [InlineData("GET", "/billing")]
    [InlineData("GET", "/billing/messages")]
    [InlineData("GET", "/billing/messages/dlq-0")]
    [InlineData("GET", "/billing/messages/dlq-0/body")]
    [InlineData("POST", "/billing/messages/dlq-0/requeue")]
    [InlineData("DELETE", "/billing/messages/dlq-0")]
    [InlineData("GET", "/-/metrics")]
    public async Task Every_surface_hides_private_resources_before_reading_state(string method, string path)
    {
        var service = Service(false);
        using var host = await Start(service);
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://localhost:5000/data/dead-letters" + path);
        using var response = await host.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        service.Verify(value => value.ReadAsync(It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(value => value.MutateAsync(It.IsAny<DeadLetterMutation>()), Times.Never);
    }

    [Fact]
    public async Task Filtered_pages_are_stable_and_never_include_headers_query_or_body()
    {
        using var host = await Start(Service(true));
        var client = host.GetTestClient();
        string url = "http://localhost:5000/data/dead-letters/billing/messages?limit=1&reason=NonRetryableResponse";
        string first = await client.GetStringAsync(url);
        using var json = JsonDocument.Parse(first);
        Assert.Equal("dlq-0", json.RootElement.GetProperty("Items")[0].GetProperty("Id").GetString());
        string cursor = json.RootElement.GetProperty("NextCursor").GetString()!;
        string second = await client.GetStringAsync(url + "&after=" + Uri.EscapeDataString(cursor));
        Assert.Contains("dlq-1", second, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-test", first, StringComparison.Ordinal);
        Assert.DoesNotContain("?private", first, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", first, StringComparison.Ordinal);
        var filtered = await client.GetStringAsync(url + "&httpStatus=402");
        Assert.Contains("dlq-2", filtered, StringComparison.Ordinal);
        Assert.DoesNotContain("dlq-0", filtered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?limit=501")]
    [InlineData("?after=invalid")]
    [InlineData("?reason=wrong")]
    [InlineData("?httpStatus=999")]
    public async Task Invalid_filters_fail_explicitly(string query)
    {
        using var host = await Start(Service(true));
        using var response = await host.GetTestClient().GetAsync("http://localhost:5000/data/dead-letters/billing/messages" + query);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_is_downloadable_inert_content_with_original_type_and_range()
    {
        using var host = await Start(Service(true));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5000/data/dead-letters/billing/messages/dlq-0/body");
        request.Headers.Range = new(0, 7);
        using var response = await host.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("<script>", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Replay_of_removed_function_is_conflict_without_mutation()
    {
        var service = Service(true);
        using var host = await Start(service);
        using var response = await host.GetTestClient().PostAsync("http://localhost:5000/data/dead-letters/billing/messages/dlq-0/requeue", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        service.Verify(value => value.MutateAsync(It.IsAny<DeadLetterMutation>()), Times.Never);
    }
}
