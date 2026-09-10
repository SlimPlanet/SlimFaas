using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Scaling;
using SlimFaas.Workers;

namespace SlimFaas.Tests.Scaling;

internal sealed class ScalingTestFixture
{
    internal sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1000);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public Clock Time { get; } = new();
    public string Name { get; } = "scaling-" + Guid.NewGuid().ToString("N");
    public RequestedMetricsRegistry Registry { get; } = new();
    public InMemoryMetricsStore Metrics { get; }
    public ExternalMetricsSourceStore Sources { get; } = new();
    public InMemoryAutoScalerStore History { get; } = new();
    public HistoryHttpMemoryService Http { get; } = new();
    public AutoScaler Scaler { get; }
    public Mock<IKubernetesService> Kubernetes { get; } = new();
    public IOptions<SlimFaasOptions> Options { get; } = Microsoft.Extensions.Options.Options.Create(new SlimFaasOptions
    { EnableFront = true, PodScaledUpByDefaultWhenInfrastructureHasNeverCalled = false });
    public ScalingDiagnosticsStore Diagnostics { get; }
    public ReplicasService Replicas { get; }
    public ScalingSimulationService Simulation { get; }
    public ScaleConfig Config { get; set; } = new()
    {
        ReplicaMax = 20, ScaleFromZero = true, Sources = [new("jobs", "http://exporter/metrics")],
        Triggers = [new(MetricName: "pending", Query: "sum(jobs_pending)", Threshold: 10, Source: "jobs")]
    };

    public ScalingTestFixture(bool diagnostics = true)
    {
        Registry.RegisterFromQuery("jobs_pending");
        Metrics = new(Registry);
        Scaler = new(new PrometheusScalerProvider(new(Metrics, TimeSpan.FromSeconds(6)), Sources), History);
        Diagnostics = new(Options, Time); Diagnostics.SetLeadership(true);
        Replicas = new(Kubernetes.Object, Http, Scaler, NullLogger<ReplicasService>.Instance, Registry, Options,
            () => Time.Now.UtcDateTime, diagnostics ? Diagnostics : null, Sources);
        Kubernetes.Setup(k => k.ScaleAsync(It.IsAny<ReplicaRequest>())).ReturnsAsync((ReplicaRequest r) => r);
        Simulation = new(Replicas, Scaler, Metrics, Sources, Options, Time);
    }

    public async Task SetFunctions(int replicas = 0, IList<DeploymentInformation>? functions = null)
    {
        functions ??= [new(Name, "default", [], new(), replicas, Scale: Config)];
        Kubernetes.Setup(k => k.ListFunctionsAsync(It.IsAny<string>(), It.IsAny<DeploymentsInformations>()))
            .ReturnsAsync(new DeploymentsInformations(functions, new(1, []), []));
        await Replicas.SyncDeploymentsAsync("default");
    }
    public void Scrape(double value, string? function = null)
    {
        var source = ExternalMetricsSource.Resolve("default", function ?? Name, Config, "jobs")!;
        long now = Time.Now.ToUnixTimeSeconds();
        Metrics.Add(now, source.Identity, "exporter", new Dictionary<string, double> { ["jobs_pending"] = value });
        Sources.Record(source, ScalerState.Valid, now);
    }
    public ScalingTriggerOverride Override(double threshold = 10, double? value = null, string query = "sum(jobs_pending)", string? source = "jobs")
        => new(0, query, source, ScaleMetricType.AverageValue, threshold, value);
    public ScalingState State() => Diagnostics.GetState(Name, 0, Replicas.Deployments.Functions[0].Replicas, 1000);
}
