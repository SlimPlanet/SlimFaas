using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace SlimFaas.Tests.Endpoints;

public sealed class TourEndpointTests
{
    private sealed class DependencyHandler(HttpStatusCode status, string ready = "ready",
        HttpStatusCode completionStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        internal List<(string Method, string Path, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(request.Method == HttpMethod.Get ? status : completionStatus)
                { Content = new StringContent(ready) };
        }
    }

    private static Task<IHost> Start(DependencyHandler handler, bool enabled = true) =>
        new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddHttpClient("internal").ConfigurePrimaryHttpMessageHandler(() => handler);
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseEndpoints(endpoints => endpoints.MapTourEndpoints("http://slimfaas", enabled));
        })).StartAsync();

    [Fact]
    public async Task Disabled_fixture_is_absent()
    {
        using var handler = new DependencyHandler(HttpStatusCode.OK);
        using var host = await Start(handler, false);
        using var response = await host.GetTestClient().PostAsync("/tour/dlq/tour-1", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "ready", HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.OK, "not-ready", HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.OK, "ready", HttpStatusCode.OK)]
    public async Task Shared_dependency_controls_replay_and_records_exact_execution(
        HttpStatusCode dependencyStatus, string flag, HttpStatusCode expected)
    {
        using var handler = new DependencyHandler(dependencyStatus, flag);
        using var host = await Start(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tour/dlq/tour-1")
        {
            Content = new StringContent("""{"orderId":"sample"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("SlimFaas-Element-Id", "replayed-execution");
        using var response = await host.GetTestClient().SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("/data/sets/tour-1-dlq-ready", handler.Requests[0].Path);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(("POST", "/data/sets/tour-1-dlq-completed?ttl=600000", "replayed-execution"), handler.Requests[1]);
            Assert.Equal("replayed-execution", await response.Content.ReadAsStringAsync());
        }
        else
        {
            Assert.Single(handler.Requests);
        }
    }

    [Fact]
    public async Task Upstream_errors_are_not_reported_as_success_or_expected_missing_dependency()
    {
        using var handler = new DependencyHandler(HttpStatusCode.ServiceUnavailable);
        using var host = await Start(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tour/dlq/tour-1")
            { Content = new StringContent("""{"orderId":"sample"}""", Encoding.UTF8, "application/json") };
        request.Headers.Add("SlimFaas-Element-Id", "original");
        await Assert.ThrowsAsync<HttpRequestException>(() => host.GetTestClient().SendAsync(request));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("invalid_id", """{"orderId":"sample"}""", true)]
    [InlineData("tour-1", "{}", true)]
    [InlineData("tour-1", """{"orderId":"sample"}""", false)]
    public async Task Invalid_requests_fail_before_accessing_shared_state(string id, string body, bool execution)
    {
        using var handler = new DependencyHandler(HttpStatusCode.OK);
        using var host = await Start(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tour/dlq/" + id)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (execution) request.Headers.Add("SlimFaas-Element-Id", "original");
        using var response = await host.GetTestClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Failure_to_persist_completion_is_not_success()
    {
        using var handler = new DependencyHandler(HttpStatusCode.OK, completionStatus: HttpStatusCode.ServiceUnavailable);
        using var host = await Start(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/tour/dlq/tour-1")
            { Content = new StringContent("""{"orderId":"sample"}""", Encoding.UTF8, "application/json") };
        request.Headers.Add("SlimFaas-Element-Id", "replayed");
        await Assert.ThrowsAsync<HttpRequestException>(() => host.GetTestClient().SendAsync(request));
        Assert.Equal(2, handler.Requests.Count);
    }
}
