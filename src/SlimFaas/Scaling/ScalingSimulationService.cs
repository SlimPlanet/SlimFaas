using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;
using SlimFaas.Workers;

namespace SlimFaas.Scaling;

public sealed class ScalingSimulationService(IReplicasService replicas, AutoScaler autoScaler,
    IMetricsStore metrics, ExternalMetricsSourceStore sources, IOptions<SlimFaasOptions> options,
    TimeProvider? clock = null)
{
    private readonly SemaphoreSlim _budget = new(2, 2);

    public async Task<ScalingSimulationResponse> SimulateAsync(ScalingSimulationRequest request, CancellationToken ct)
    {
        if (!await _budget.WaitAsync(0, ct)) throw new ScalingSimulationException(429, "Two simulations are already running. Retry shortly.");
        var requestCancellation = ct;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        ct = deadline.Token;
        try
        {
            if (replicas is not ReplicasService service)
                throw new ScalingSimulationException(503, "Scaling context is not available.");
            var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var environment = service.CaptureSimulationEnvironment(now);
            var function = environment.Deployments.Functions.FirstOrDefault(f => f.Deployment == request.Function)
                ?? throw new ScalingSimulationException(404, "Function not found.");
            var scale = function.Scale ?? new ScaleConfig();
            Validate(request, scale);
            var overrides = (request.Triggers ?? []).ToDictionary(t => t.Index);
            var simulatedScale = scale with
            {
                ReplicaMax = request.ClearReplicaMax ? null : request.ReplicaMax ?? scale.ReplicaMax,
                ScaleFromZero = request.ScaleFromZero ?? scale.ScaleFromZero,
                Behavior = request.Behavior ?? scale.Behavior,
                Triggers = scale.Triggers.Select((t, i) => overrides.TryGetValue(i, out var change)
                    ? t with { Query = change.Query, Source = change.Source, Threshold = change.Threshold, MetricType = change.MetricType }
                    : t).ToArray()
            };
            var variant = function with { Scale = simulatedScale, Replicas = request.CurrentReplicas ?? function.Replicas };
            // Capture once. Both comparisons use these same metric points, source health and decision histories.
            var health = sources.Capture();
            var history = autoScaler.CaptureHistory(function.Deployment);
            var identities = environment.Deployments.Functions.SelectMany(f => (f.Scale?.Sources ?? []).Where(s => s is not null)
                .Select(s => ExternalMetricsSource.Resolve(f.Namespace, f.Deployment, f.Scale!, s.Name)?.Identity))
                .Where(id => id is not null).Select(id => id!).ToHashSet(StringComparer.Ordinal);
            identities.Add(function.Deployment); identities.Add("slimfaas");
            var frozen = FrozenScalingMetrics.Capture(metrics, identities, ct, new DateTimeOffset(now).ToUnixTimeSeconds());
            int interval = options.Value.MetricsScraping.ScrapeIntervalMilliseconds;
            var evaluator = new PromQlMiniEvaluator(frozen, TimeSpan.FromMilliseconds(3L * interval));
            var provider = new PrometheusScalerProvider(evaluator, health, interval);
            var evaluations = new Dictionary<string, ScalerEvaluation>(StringComparer.Ordinal);
            foreach (var other in environment.Deployments.Functions)
            {
                ct.ThrowIfCancellationRequested();
                if (other.Scale?.Triggers.Count > 0 && (other.Replicas > 0 || other.Scale.ScaleFromZero))
                    evaluations[other.Deployment] = await Evaluate(other, provider, now,
                        other.Deployment != function.Deployment || other.Replicas == 0, null, ct);
            }
            var current = Calculate(function, environment, evaluations, history, health, interval);
            var variantEvaluations = new Dictionary<string, ScalerEvaluation>(evaluations, StringComparer.Ordinal);
            variantEvaluations.Remove(function.Deployment);
            if (variant.Scale?.Triggers.Count > 0 && (variant.Replicas > 0 || variant.Scale.ScaleFromZero))
                variantEvaluations[variant.Deployment] = await Evaluate(variant, provider, now, variant.Replicas == 0, overrides, ct);
            var result = Calculate(variant, environment, variantEvaluations, history, health, interval);
            result = result with
            {
                Triggers = result.Triggers.Select(t => t with
                {
                    Simulated = overrides.TryGetValue(t.Index, out var change) && change.Value.HasValue,
                    Detail = t.State == "InvalidMetric" ? "No usable matching data, an invalid value or an invalid query. Only already collected metrics are available." : null
                }).ToArray()
            };
            return new(new DateTimeOffset(now).ToUnixTimeMilliseconds(), current with { Application = "Preview" },
                result with { Application = "Preview" },
                ["One decision only; dependency readiness, HTTP activity and histories are frozen at capture time.",
                 "Queries use already collected metrics. No exporter is contacted and no metric is registered.",
                 "Preview targets are not sent to the orchestrator; future scheduling success is not predicted."]);
        }
        catch (OperationCanceledException) when (!requestCancellation.IsCancellationRequested)
        { throw new ScalingSimulationException(503, "Simulation exceeded its five-second time budget."); }
        finally { _budget.Release(); }
    }

    private static ScalingDecision Calculate(DeploymentInformation function, ScalingEnvironment environment,
        IReadOnlyDictionary<string, ScalerEvaluation> evaluations, ScalingHistory history,
        ExternalMetricsSourceStore health, int interval)
    {
        var deployments = environment.Deployments with { Functions = environment.Deployments.Functions
            .Select(f => f.Deployment == function.Deployment ? function : f).ToList() };
        var context = ReplicasService.CaptureContext(deployments, function, environment.NowUtc, environment.HttpTicks,
            environment.HttpTicks.Values.DefaultIfEmpty(0).Max(), ReplicasService.GetExternalDependencyDemand(deployments, evaluations),
            environment.TurnOnByDefault);
        evaluations.TryGetValue(function.Deployment, out var evaluation);
        var calculation = ScalingDecisionCalculator.NeedsMetrics(context, evaluation)
            ? MetricsScalingCalculator.Calculate(function.Scale, function.Replicas, function.ReplicasMin,
                function.Scale?.ReplicaMax, context.NowSeconds, evaluation!, history) : null;
        var sourceDiagnostics = (function.Scale?.Sources ?? []).Where(s => s is not null).Select(s =>
        {
            var resolved = ExternalMetricsSource.Resolve(function.Namespace, function.Deployment, function.Scale!, s.Name);
            var observation = resolved is null ? null : health.Get(resolved.Identity);
            int frequency = function.Scale!.ScrapeIntervalMilliseconds ?? interval;
            string state = resolved is null ? "Misconfigured" : observation!.State == ScalerState.Valid &&
                context.NowSeconds - observation.LastSuccess >= frequency * 3 / 1000.0 ? "Stale" : observation!.State.ToString();
            return new ScalingSourceDiagnostic(s.Name, state, observation?.LastSuccess > 0 ? observation.LastSuccess * 1000 : null, frequency);
        }).ToArray();
        return ScalingDecisionCalculator.Calculate(context, evaluation, calculation, sourceDiagnostics);
    }

    private static async Task<ScalerEvaluation> Evaluate(DeploymentInformation function, IScalerProvider provider,
        DateTime now, bool externalOnly, IReadOnlyDictionary<int, ScalingTriggerOverride>? overrides, CancellationToken ct)
    {
        var results = new List<ScalerTriggerResult>();
        for (int i = 0; i < function.Scale!.Triggers.Count; i++)
        {
            var trigger = function.Scale.Triggers[i];
            if (externalOnly && trigger.Source is null) continue;
            ScalerResult value;
            if (overrides is not null && overrides.TryGetValue(i, out var change) && change.Value is { } fake)
                value = new(ScalerState.Valid, fake, fake > 0);
            else
            {
                try
                {
                    value = await provider.GetAsync(new(function.Namespace, function.Deployment, function.Scale,
                        trigger, new DateTimeOffset(now).ToUnixTimeSeconds()), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (OperationCanceledException) { value = new(ScalerState.Timeout); }
                catch (Exception) { value = new(ScalerState.Unavailable); }
            }
            results.Add(new(trigger, value, i));
        }
        return new(results);
    }

    internal static void Validate(ScalingSimulationRequest request, ScaleConfig scale)
    {
        static void Require(bool valid, string message)
        { if (!valid) throw new ScalingSimulationException(400, message); }
        Require(!string.IsNullOrWhiteSpace(request.Function) && request.Function.Length <= 253, "A configured function is required.");
        Require(request.CurrentReplicas is null or >= 0 && request.ReplicaMax is null or >= 0, "Replica counts must be non-negative integers.");
        Require(!(request.ClearReplicaMax && request.ReplicaMax.HasValue), "Choose a replica maximum or clear it, not both.");
        Require((request.Triggers?.Count ?? 0) <= 32, "At most 32 trigger overrides are supported.");
        var indices = new HashSet<int>();
        foreach (var t in request.Triggers ?? [])
        {
            Require(t is not null && t.Index >= 0 && t.Index < scale.Triggers.Count && indices.Add(t.Index), "Trigger indices must be configured and unique.");
            Require(t!.Source is null || ExternalMetricsSource.Resolve("", "", scale, t.Source) is not null, "Choose a configured, unambiguous source.");
            Require(double.IsFinite(t.Threshold) && t.Threshold > 0, "Thresholds must be finite and positive.");
            Require(t.Value is null || double.IsFinite(t.Value.Value) && t.Value >= 0, "Simulated values must be finite and non-negative.");
            Require(Enum.IsDefined(t.MetricType), "Unsupported metric type.");
            Require(!string.IsNullOrWhiteSpace(t.Query) && t.Query.Length <= 2048, "Queries must contain 1–2048 characters.");
            // Bound parser recursion and expression depth for browser-supplied queries.
            Require(t.Query!.Count(c => c is '(' or '[' or '{' or '+' or '-' or '*' or '/') <= 64, "Query is too complex for the playground.");
            try { _ = PromQlQueryCompiler.Compile(t.Query!); }
            catch (Exception error) when (error is FormatException or ArgumentException)
            { throw new ScalingSimulationException(400, "Invalid PromQL query or label regular expression."); }
        }
        if (request.Behavior is not { } behavior) return;
        foreach (var direction in new[] { behavior.ScaleUp, behavior.ScaleDown })
        {
            Require(direction is not null && direction.StabilizationWindowSeconds is >= 0 and <= 86400 &&
                direction.Policies is not null && direction.Policies.Count <= 16, "Invalid stabilization window or policy list.");
            foreach (var policy in direction!.Policies!)
                Require(policy is not null && Enum.IsDefined(policy.Type) && policy.Value is > 0 and <= 100000 &&
                    policy.PeriodSeconds is >= 0 and <= 86400, "Invalid scaling policy.");
        }
    }
}

public sealed class ScalingSimulationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// Bounded indexed copy; evaluator callbacks cannot observe later writes to the real store.
internal sealed class FrozenScalingMetrics : IMetricsStore
{
    private sealed record Series(int Id, string Deployment, string Pod, string Key, MetricPoint[] Points);
    private readonly List<Series> _series = [];
    private CancellationToken _cancellation;
    public long? LatestTimestamp { get; private set; }
    public int SeriesCount => _series.Count;
    internal static FrozenScalingMetrics Capture(IMetricsStore source, IReadOnlySet<string> scopes, CancellationToken ct, long now = long.MaxValue)
    {
        var copy = new FrozenScalingMetrics { _cancellation = ct };
        long bytes = 0;
        source.VisitSeries((id, deployment, pod, key, points) =>
        {
            ct.ThrowIfCancellationRequested();
            if (!scopes.Contains(deployment)) return;
            bytes += 16L * points.Count + 2L * (deployment.Length + pod.Length + key.Length) + 256;
            if (bytes > 8 * 1024 * 1024) throw new ScalingSimulationException(503, "The collected dataset exceeds the 8 MiB simulation budget.");
            var captured = points.Where(p => p.Timestamp <= now).ToArray();
            copy._series.Add(new(id, deployment, pod, key, captured));
            foreach (var point in captured) copy.LatestTimestamp = Math.Max(copy.LatestTimestamp ?? point.Timestamp, point.Timestamp);
        });
        return copy;
    }
    public void VisitSeries(MetricsSeriesVisitor visitor)
    { foreach (var series in _series) { _cancellation.ThrowIfCancellationRequested(); visitor(series.Id, series.Deployment, series.Pod, series.Key, series.Points); } }
    public void Add(long timestamp, string deployment, string podIp, IReadOnlyDictionary<string, double> metrics) => throw new NotSupportedException();
    public IReadOnlyDictionary<long, IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>>>> Snapshot() => throw new NotSupportedException();
    public MetricsStoreRecord CreateRecord() => throw new NotSupportedException();
    public void ReplaceFromRecord(MetricsStoreRecord record) => throw new NotSupportedException();
}
