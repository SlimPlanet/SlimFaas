using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;

namespace SlimFaas.Tests;

public sealed class BoundedHttpClientMetricsTests
{
    [Fact]
    public async Task RotatingPodHostsDoNotCreateNewMetricSeries()
    {
        var registry = Metrics.NewCustomRegistry();
        var services = new ServiceCollection();
        services
            .AddHttpClient("rotating-pods")
            .ConfigurePrimaryHttpMessageHandler(static () => new ImmediateHandler())
            .UseHttpClientMetrics(BoundedHttpClientMetrics.Create(registry));

        await using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient("rotating-pods");

        for (var index = 0; index < 1_000; index++)
        {
            using var response = await client.GetAsync(
                $"http://pod-{index}.ephemeral.test/health",
                HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
        }

        using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream);
        string exposition = System.Text.Encoding.UTF8.GetString(
            stream.GetBuffer(),
            0,
            checked((int)stream.Length));
        string[] lines = exposition.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.DoesNotContain("host=\"", exposition, StringComparison.Ordinal);
        Assert.Single(lines, static line =>
            line.StartsWith("httpclient_requests_sent_total{", StringComparison.Ordinal));
        Assert.Equal(
            40,
            lines.Count(static line => line.StartsWith("httpclient_", StringComparison.Ordinal)));
    }

    private sealed class ImmediateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([])
                });
    }
}
