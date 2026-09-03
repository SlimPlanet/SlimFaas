using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Endpoints;

public sealed class DebugPromQlEndpointTests
{
    [Fact]
    public async Task EvaluateWithSourceUsesTheConfiguredExternalScope()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string function = "worker";
        const string source = "queue";
        string scope = MetricsSourceScope.ForExternal(function, source);
        PromQlMiniEvaluator.SnapshotProvider snapshots = () =>
            new Dictionary<long, IReadOnlyDictionary<string,
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>>
            {
                [now] = new Dictionary<string,
                    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>
                {
                    [function] = TargetWithMetric(99),
                    [scope] = TargetWithMetric(3)
                }
            };
        var health = new ExternalMetricsSourceHealthRegistry();
        health.RecordSuccess(scope, now, 1_000, ["queue_depth"]);
        var deployment = new DeploymentInformation(
            function,
            "default",
            [],
            new SlimFaasConfiguration(),
            Replicas: 0,
            Scale: new ScaleConfig
            {
                Sources = [new ScaleSource(source, "https://queue.example.test/metrics")],
                Triggers = [new ScaleTrigger(Query: "queue_depth", Threshold: 1, Source: source)]
            });

        using IHost host = await new HostBuilder()
            .ConfigureWebHost(webBuilder => webBuilder
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton(new PromQlMiniEvaluator(snapshots));
                    services.AddSingleton<IMetricsScrapingGuard, MetricsScrapingGuard>();
                    services.AddSingleton<IRequestedMetricsRegistry, RequestedMetricsRegistry>();
                    services.AddSingleton<IExternalMetricsSourceHealthRegistry>(health);
                    services.AddSingleton<IReplicasService>(new TestReplicasService(deployment));
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapDebugRoutes());
                }))
            .StartAsync();
        using var body = new StringContent(
            $$"""{"query":"queue_depth","deployment":"{{function}}","source":"{{source}}","nowUnixSeconds":{{now}}}""",
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response = await host.GetTestClient().PostAsync("/debug/promql/eval", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, json.RootElement.GetProperty("value").GetDouble());
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> TargetWithMetric(
        double value) =>
        new Dictionary<string, IReadOnlyDictionary<string, double>>
        {
            ["target"] = new Dictionary<string, double> { ["queue_depth"] = value }
        };

    private sealed class TestReplicasService(DeploymentInformation deployment) : IReplicasService
    {
        public DeploymentsInformations Deployments { get; } = new(
            [deployment],
            new SlimFaasDeploymentInformation(1, []),
            []);

        public Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace) =>
            Task.FromResult(Deployments);

        public Task CheckScaleAsync(string kubeNamespace) => Task.CompletedTask;
    }
}
