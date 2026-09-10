using System.Text;
using System.Text.Json;
using MemoryPack;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Kubernetes;

public class ExternalScalerTests
{
    private const long Now = 1_000;
    private static ScaleConfig Config(bool wake = true) => new()
    {
        ReplicaMax = 20,
        ScaleFromZero = wake,
        ScrapeIntervalMilliseconds = 5000,
        Sources = [new("jobs", "http://exporter:9090/metrics")],
        Triggers = [new(Query: "sum(jobs_pending{queue=\"emails\"})", Threshold: 10, Source: "jobs")],
        Behavior = new()
        {
            ScaleUp = new() { Policies = [new(ScalePolicyType.Pods, 20, 15)] },
            ScaleDown = new() { Policies = [new(ScalePolicyType.Percent, 100, 15)] }
        }
    };

    private static DeploymentInformation Function(ScaleConfig config, int replicas = 0) =>
        new("worker", "default", [], new(), replicas, Scale: config);

    private sealed class Fixture
    {
        public RequestedMetricsRegistry Registry { get; } = new();
        public InMemoryMetricsStore Store { get; }
        public ExternalMetricsSourceStore Health { get; } = new();
        public PromQlMiniEvaluator Evaluator { get; }
        public PrometheusScalerProvider Provider { get; }

        public Fixture(bool useSnapshot = false)
        {
            Registry.RegisterFromQuery("jobs_pending");
            Store = new(Registry);
            Evaluator = useSnapshot
                ? new(() => Store.Snapshot(), TimeSpan.FromSeconds(6))
                : new(Store, TimeSpan.FromSeconds(6));
            Provider = new(Evaluator, Health);
        }

        public void Scrape(ScaleConfig config, double value, long now = Now, string function = "worker", string ns = "default")
        {
            var source = ExternalMetricsSource.Resolve(ns, function, config, "jobs")!;
            Store.Add(now, source.Identity, "exporter", new Dictionary<string, double>
            {
                ["jobs_pending{queue=\"emails\"}"] = value
            });
            Health.Record(source, ScalerState.Valid, now);
        }

        public ValueTask<ScalerResult> Evaluate(ScaleConfig config, long now = Now) =>
            Provider.GetAsync(new("default", "worker", config, config.Triggers[0], now), default);
    }

    [Fact]
    public void ExistingConfigurationKeepsDefaultsAndNewFieldsRoundTripWithGeneratedJson()
    {
        var legacy = JsonSerializer.Deserialize("{\"Triggers\":[{\"Query\":\"jobs_pending\",\"Threshold\":10}]}",
            ScaleConfigSerializerContext.Default.ScaleConfig)!;
        legacy = FunctionMetadataParser.NormalizeScaleConfig(legacy);
        Assert.False(legacy.ScaleFromZero);
        Assert.Empty(legacy.Sources);
        Assert.Null(legacy.Triggers[0].Source);
        var json = JsonSerializer.Serialize(Config(), ScaleConfigSerializerContext.Default.ScaleConfig);
        var parsed = FunctionMetadataParser.ParseScale(new Dictionary<string, string> { ["SlimFaas/Scale"] = json })!;
        Assert.True(parsed.ScaleFromZero);
        Assert.Equal("jobs", parsed.Triggers[0].Source);
        Assert.Single(parsed.Sources);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 8)]
    public async Task ExternalWakeRequiresOptIn(bool enabled, int expected)
    {
        var f = new Fixture();
        var config = Config(enabled);
        f.Scrape(config, 73);
        var (service, _, history) = await Service(f.Provider, [Function(config)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(expected, service.Deployments.Functions[0].Replicas);
        Assert.Equal(0, history.GetTicksLastCall("worker"));
    }

    [Theory]
    [InlineData(ScaleMetricType.AverageValue, 0, 8)]
    [InlineData(ScaleMetricType.AverageValue, 2, 8)]
    [InlineData(ScaleMetricType.Value, 0, 8)]
    [InlineData(ScaleMetricType.Value, 2, 15)]
    public async Task ExternalTriggersUseExistingFormulas(ScaleMetricType type, int current, int expected)
    {
        var f = new Fixture();
        var config = Config();
        config.Triggers[0] = config.Triggers[0] with { MetricType = type };
        f.Scrape(config, 73);
        var (service, _, _) = await Service(f.Provider, [Function(config, current)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(expected, service.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task DefaultPoliciesLimitFirstWakeToFour()
    {
        var f = new Fixture();
        var config = Config() with { Behavior = new() };
        f.Scrape(config, 73);
        var (service, _, _) = await Service(f.Provider, [Function(config)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(4, service.Deployments.Functions[0].Replicas);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceIsolationIncludesFunctionNamespaceUrlAndLegacyDebugQueries(bool useSnapshot)
    {
        var f = new Fixture(useSnapshot);
        var config = Config();
        f.Scrape(config, 73);
        f.Scrape(config, 500, function: "another-worker");
        f.Scrape(config, 900, ns: "other-namespace");
        f.Store.Add(Now, "worker", "pod", new Dictionary<string, double> { ["jobs_pending{queue=\"emails\"}"] = 30 });
        Assert.Equal(73, (await f.Evaluate(config)).Value);
        Assert.Equal(30, f.Evaluator.Evaluate(config.Triggers[0].Query, Now, "worker"));
        Assert.Equal(30, f.Evaluator.Evaluate(config.Triggers[0].Query, Now));
        var changed = config with { Sources = [new("jobs", "http://exporter:9090/new-metrics")] };
        Assert.Equal(ScalerState.Unavailable, (await f.Evaluate(changed)).State);
    }

    [Theory]
    [InlineData("http://exporter:9090/metrics", "missing")]
    [InlineData("file:///tmp/metrics", "jobs")]
    [InlineData("https://user:password@exporter/metrics", "jobs")]
    [InlineData("not-a-url", "jobs")]
    public async Task BadSourceIsMisconfiguredWithoutDisablingScale(string url, string name)
    {
        var config = Config() with { Sources = [new("jobs", url)] };
        config.Triggers[0] = config.Triggers[0] with { Source = name };
        var json = JsonSerializer.Serialize(config, ScaleConfigSerializerContext.Default.ScaleConfig);
        var parsed = FunctionMetadataParser.ParseScale(new Dictionary<string, string> { ["SlimFaas/Scale"] = json });
        Assert.NotNull(parsed);
        Assert.Equal(ScalerState.Misconfigured, (await new Fixture().Evaluate(parsed)).State);
    }

    [Fact]
    public async Task DuplicateSourceNamesAreMisconfigured()
    {
        var config = Config();
        config.Sources.Add(config.Sources[0]);
        Assert.Equal(ScalerState.Misconfigured, (await new Fixture().Evaluate(config)).State);
    }

    [Theory]
    [InlineData((int)ScalerState.Unavailable)]
    [InlineData((int)ScalerState.Timeout)]
    [InlineData((int)ScalerState.InvalidMetric)]
    public async Task FailedScrapeImmediatelyInvalidatesPreviouslyValidZero(int state)
    {
        var f = new Fixture();
        var config = Config();
        f.Scrape(config, 0);
        f.Health.Record(ExternalMetricsSource.Resolve("default", "worker", config, "jobs")!, (ScalerState)state, Now);
        var (service, _, _) = await Service(f.Provider, [Function(config, 8)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(8, service.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task FreshnessUsesSourceCadenceAndAlsoProtectsRangeQueries()
    {
        var f = new Fixture();
        var config = Config();
        config.Triggers[0] = config.Triggers[0] with { Query = "max_over_time(jobs_pending[1m])" };
        f.Scrape(config, 0);
        Assert.Equal(ScalerState.Valid, (await f.Evaluate(config, Now + 14)).State);
        Assert.Equal(ScalerState.Stale, (await f.Evaluate(config, Now + 15)).State);
    }

    [Fact]
    public async Task MissingSelectorInLatestScrapeCannotReuseHistoricalZero()
    {
        var f = new Fixture();
        var config = Config();
        config.Triggers[0] = config.Triggers[0] with { Query = "max_over_time(jobs_pending{queue=\"emails\"}[1m])" };
        f.Scrape(config, 0);
        var source = ExternalMetricsSource.Resolve("default", "worker", config, "jobs")!;
        f.Store.Add(Now + 5, source.Identity, "exporter", new Dictionary<string, double> { ["jobs_pending{queue=\"other\"}"] = 0 });
        f.Health.Record(source, ScalerState.Valid, Now + 5);
        Assert.Equal(ScalerState.InvalidMetric, (await f.Evaluate(config, Now + 5)).State);
    }

    [Fact]
    public async Task SnapshotIsBackwardCompatibleButRestoredHealthRequiresFreshScrape()
    {
        var f = new Fixture();
        var config = Config();
        f.Scrape(config, 73);
        var bytes = MemoryPackSerializer.Serialize(f.Store.CreateRecord());
        var restored = new Fixture();
        restored.Store.ReplaceFromRecord(MemoryPackSerializer.Deserialize<MetricsStoreRecord>(bytes)!);
        Assert.Equal(f.Store.SeriesCount, restored.Store.SeriesCount);
        Assert.Equal(ScalerState.Unavailable, (await restored.Evaluate(config)).State);
        restored.Scrape(config, 73);
        Assert.Equal(73, (await restored.Evaluate(config)).Value);
        restored.Health.Reset();
        Assert.Equal(ScalerState.Unavailable, (await restored.Evaluate(config)).State);
    }

    [Fact]
    public async Task LocalAbsenceDoesNotBlockExternalWakeAndExistingLocalSamplesCannotCauseWake()
    {
        var f = new Fixture();
        var config = Config();
        config.Triggers.Add(new(Query: "jobs_pending", Threshold: 1));
        f.Scrape(config, 73);
        var (service, _, _) = await Service(f.Provider, [Function(config)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(8, service.Deployments.Functions[0].Replicas);
        f.Scrape(config, 0);
        f.Store.Add(Now, "worker", "pod", new Dictionary<string, double> { ["jobs_pending"] = 99 });
        (service, _, _) = await Service(f.Provider, [Function(config)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(0, service.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task DependenciesWakeBeforeWorkerWithoutConsumingItsPolicyBudget()
    {
        var f = new Fixture();
        var config = Config();
        f.Scrape(config, 73);
        var worker = Function(config) with { DependsOn = ["database"] };
        var database = new DeploymentInformation("database", "default", [], new(), 0);
        var policyStore = new InMemoryAutoScalerStore();
        var (service, kube, history) = await Service(f.Provider, [worker, database], policyStore);
        await service.CheckScaleAsync("default");
        Assert.Equal(0, service.Deployments.Functions[0].Replicas);
        Assert.Equal(1, service.Deployments.Functions[1].Replicas);
        Assert.Empty(policyStore.GetSamples("worker", 0));
        Assert.Equal(0, history.GetTicksLastCall("database"));
        var ready = database with { Replicas = 1, Pods = [new("db-0", true, true, "127.0.0.1", "database")] };
        kube.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(new DeploymentsInformations([worker, ready], new(1, []), []));
        await service.SyncDeploymentsAsync("default");
        await service.CheckScaleAsync("default");
        Assert.Equal(8, service.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task HttpWakeIsCombinedWithExternalWakeAndRespectsMaximum()
    {
        var f = new Fixture();
        var config = Config() with { ReplicaMax = 10 };
        f.Scrape(config, 73);
        var (service, _, history) = await Service(f.Provider, [Function(config) with { ReplicasAtStart = 12 }]);
        history.SetTickLastCall("worker", DateTimeOffset.FromUnixTimeSeconds(Now).UtcDateTime.Ticks);
        await service.CheckScaleAsync("default");
        Assert.Equal(10, service.Deployments.Functions[0].Replicas);
    }

    [Fact]
    public async Task EngineCombinesAsyncProviderResultsWithoutPrometheusAndHoldsOnFailure()
    {
        var provider = new DelegateProvider(async context =>
        {
            await Task.Yield();
            if (context.Trigger.MetricName == "failed") throw new HttpRequestException();
            return new(ScalerState.Valid, context.Trigger.MetricName == "large" ? 120 : 0, true);
        });
        var config = Config();
        config = config with { Triggers = [new(MetricName: "large", Threshold: 10), new(MetricName: "failed", Threshold: 10)] };
        var (service, _, _) = await Service(provider, [Function(config, 8)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(12, service.Deployments.Functions[0].Replicas);
        config.Triggers[0] = config.Triggers[0] with { MetricName = "zero" };
        await service.CheckScaleAsync("default");
        Assert.Equal(12, service.Deployments.Functions[0].Replicas);
    }

    [Theory]
    [InlineData("jobs_pending NaN\n")]
    [InlineData("jobs_pending 0\njobs_pending{queue=\"emails\"} +Inf\n")]
    [InlineData("jobs_pending broken\n")]
    public async Task ExternalParserRejectsInvalidSelectedSamplesAtomically(string body)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var parsed = await PrometheusStreamParser.ParseAsync(stream, ["jobs_pending"], new(), default, rejectInvalidSamples: true);
        Assert.Equal(PrometheusStreamParseStatus.InvalidSample, parsed.Status);
        Assert.Empty(parsed.Metrics);
    }

    private sealed class DelegateProvider(Func<ScalerContext, Task<ScalerResult>> get) : IScalerProvider
    {
        public async ValueTask<ScalerResult> GetAsync(ScalerContext context, CancellationToken cancellationToken)
            => await get(context);
    }

    [Fact]
    public void ExternalScrapeAdmissionIsAtomicAtStoreCapacity()
    {
        var registry = new RequestedMetricsRegistry();
        registry.RegisterFromQuery("jobs_pending");
        var store = new InMemoryMetricsStore(registry, maxSeries: 2);
        store.Add(Now, "existing", "pod", new Dictionary<string, double> { ["jobs_pending"] = 1 });
        Assert.False(store.TryAddComplete(Now, "external", "exporter", new Dictionary<string, double>
        {
            ["jobs_pending{shard=\"a\"}"] = 20,
            ["jobs_pending{shard=\"b\"}"] = 80
        }));
        Assert.Equal(1, store.SeriesCount);
        Assert.True(store.TryAddComplete(Now, "external", "exporter", new Dictionary<string, double> { ["jobs_pending"] = 100 }));
        Assert.Equal(2, store.SeriesCount);
    }

    [Fact]
    public async Task VeryLargeFiniteExternalValueCannotOverflowIntoScaleDown()
    {
        var f = new Fixture();
        var config = Config();
        f.Scrape(config, double.MaxValue);
        var (service, _, _) = await Service(f.Provider, [Function(config, 8)]);
        await service.CheckScaleAsync("default");
        Assert.Equal(20, service.Deployments.Functions[0].Replicas);
    }

    private static async Task<(ReplicasService, Mock<IKubernetesService>, HistoryHttpMemoryService)> Service(
        IScalerProvider provider, IList<DeploymentInformation> functions, IAutoScalerStore? policyStore = null)
    {
        var kube = new Mock<IKubernetesService>();
        kube.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(new DeploymentsInformations(functions, new(1, []), []));
        kube.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest request) => request);
        var history = new HistoryHttpMemoryService();
        var service = new ReplicasService(kube.Object, history, new AutoScaler(provider, policyStore ?? new InMemoryAutoScalerStore()),
            NullLogger<ReplicasService>.Instance, new RequestedMetricsRegistry(),
            Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions { PodScaledUpByDefaultWhenInfrastructureHasNeverCalled = false }),
            () => DateTimeOffset.FromUnixTimeSeconds(Now).UtcDateTime);
        await service.SyncDeploymentsAsync("default");
        return (service, kube, history);
    }
}
