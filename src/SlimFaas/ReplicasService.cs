using SlimFaas.Kubernetes;
using NodaTime;
using System.Globalization;
using Microsoft.Extensions.Options;
using SlimFaas.Options;
using SlimFaas.Scaling;
using SlimFaas.Streams;

using ZLinq;

namespace SlimFaas;

public interface IReplicasService
{
    DeploymentsInformations Deployments { get; }
    Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace);
    Task CheckScaleAsync(string kubeNamespace);
}

public class ReplicasService(
    IKubernetesService kubernetesService,
    HistoryHttpMemoryService historyHttpService,
    AutoScaler autoScaler,
    ILogger<ReplicasService> logger,
    IRequestedMetricsRegistry metricsRegistry,
    IOptions<SlimFaasOptions> slimFaasOptions,
    Func<DateTime>? nowProvider = null,
    ScalingDiagnosticsStore? diagnostics = null,
    ExternalMetricsSourceStore? externalSources = null,
    StreamDemandStore? streamDemand = null)
    : IReplicasService
{
    private readonly bool _isTurnOnByDefault = slimFaasOptions.Value.PodScaledUpByDefaultWhenInfrastructureHasNeverCalled;

    private readonly Func<DateTime> _nowProvider = nowProvider ?? (() => DateTime.UtcNow);

    // On part du principe que DeploymentsInformations est (quasi) immuable.
    private DeploymentsInformations _deployments = new(
        new List<DeploymentInformation>(),
        new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
        new List<PodInformation>());

    // Lecture sans verrou puisque _deployments est remplacé atomiquement.
    // The returned snapshot must never be mutated by consumers.
    public DeploymentsInformations Deployments => Volatile.Read(ref _deployments);

    // souvent Reason = "Unschedulable", message contenant "exceeded quota", "Insufficient cpu", etc.
    static bool IsInfrastructureFailure(PodInformation pod) =>
        !string.IsNullOrEmpty(pod.StartFailureReason);

    public async Task<DeploymentsInformations> SyncDeploymentsAsync(string kubeNamespace)
    {
        DeploymentsInformations deployments = await kubernetesService.ListFunctionsAsync(kubeNamespace, Deployments);

        // 🔍 Enregistrer les métriques utilisées par les triggers
        foreach (var f in deployments.Functions)
        {
            if (f.Scale?.Triggers is null)
                continue;

            foreach (var trigger in f.Scale.Triggers)
            {
                if (!string.IsNullOrWhiteSpace(trigger.Query))
                    metricsRegistry.RegisterFromQuery(trigger.Query);
            }
        }

        // Remplacement atomique de l'instance.
        Interlocked.Exchange(ref _deployments, deployments);
        return deployments;
    }

    public async Task CheckScaleAsync(string kubeNamespace)
    {
        var currentDeployments = _deployments;
        string? diagnosticSession = diagnostics?.Session;
        var nowUtc = _nowProvider();
        long nowUnixSeconds = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();

        long maximumTicks = 0L;
        var ticksLastCall = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (DeploymentInformation deploymentInformation in currentDeployments.Functions)
        {
            long tickLastCall = historyHttpService.GetTicksLastCall(deploymentInformation.Deployment);
            ticksLastCall.Add(deploymentInformation.Deployment, tickLastCall);
            maximumTicks = Math.Max(maximumTicks, tickLastCall);
        }

        // Read signals once, before dependency ordering, without consuming any scale policies.
        var evaluations = new Dictionary<string, ScalerEvaluation>(StringComparer.Ordinal);
        foreach (var function in currentDeployments.Functions)
        {
            if (function.Scale?.Triggers.Count > 0 &&
                (function.Replicas > 0 || function.Scale.ScaleFromZero))
                evaluations[function.Deployment] = await autoScaler.EvaluateAsync(function, nowUnixSeconds,
                    externalOnly: function.Replicas == 0);
        }
        var streamWork = streamDemand?.Capture();
        var dependencyDemand = GetExternalDependencyDemand(currentDeployments, evaluations, streamWork);
        diagnostics?.Retain(currentDeployments.Functions.AsValueEnumerable()
            .Select(static f => f.Deployment).ToHashSet(StringComparer.Ordinal));

        List<Task<ReplicaRequest?>> tasks = new();

        foreach (DeploymentInformation deploymentInformation in currentDeployments.Functions)
        {
            var context = CaptureContext(currentDeployments, deploymentInformation, nowUtc,
                ticksLastCall, maximumTicks, dependencyDemand, _isTurnOnByDefault) with
                { StreamDemand = streamWork?.GetValueOrDefault(deploymentInformation.Deployment) ?? 0 };
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogTimeLeftWithoutRequestForScale(deploymentInformation.Deployment, TimeSpan.FromTicks(context.EffectiveActivityTicks)
                        + TimeSpan.FromSeconds(context.TimeoutSeconds) - TimeSpan.FromTicks(nowUtc.Ticks));
            int currentScale = deploymentInformation.Replicas;
            evaluations.TryGetValue(deploymentInformation.Deployment, out var evaluation);
            var combined = StreamScaling.Include(context.Function, evaluation, streamWork);
            context = context with { Function = combined.Function };
            evaluation = combined.Evaluation;
            MetricsScalingDecision? metrics = null;
            if (ScalingDecisionCalculator.NeedsMetrics(context, evaluation))
                metrics = autoScaler.ComputeDecision(combined.Function, nowUnixSeconds, evaluation!, recordDecision: false);
            var decision = ScalingDecisionCalculator.Calculate(context, evaluation, metrics,
                SourceDiagnostics(deploymentInformation, nowUnixSeconds));
            if (decision.Reasons.Any(r => r.Code == "InfrastructureBlocked"))
            {
                var failure = HasInfrastructurePodFailure(deploymentInformation);
                logger.LogSkipScaleUpForBecausePod(deploymentInformation.Deployment, failure?.Reason, failure?.Message);
            }
            int desiredReplicas = decision.Target;
            diagnostics?.Record(decision, deploymentInformation.Scale, diagnosticSession);

            if (desiredReplicas == currentScale)
            {
                continue;
            }

            logger.LogScaleFromTo(deploymentInformation.Deployment, currentScale, desiredReplicas);

            // An increase the metric policies did not produce (wake-up to ReplicasAtStart on
            // HTTP, schedule or dependency activity, external wake-up beyond the metric
            // target) must not consume the scale-up policy budget: the first metric-driven
            // scale-out after a scale-from-zero would otherwise wait a full policy period
            // (issue #370). The change is still recorded with its previous count, so the
            // scale-down budgets of #350 keep seeing it as an accepted addition; reductions
            // are never flagged.
            bool wakeUp = desiredReplicas > currentScale && (metrics is null || desiredReplicas != metrics.Target);
            tasks.Add(ApplyScaleAsync(decision, deploymentInformation.Scale, diagnosticSession, wakeUp, new ReplicaRequest(
                Replicas: desiredReplicas,
                Deployment: deploymentInformation.Deployment,
                Namespace: kubeNamespace,
                PodType: deploymentInformation.PodType
            )));
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks);
            }
            finally
            {
                // Preserve successful writes even if another function's write failed.
                var requestsByDeployment = tasks.AsValueEnumerable().Where(static t => t.IsCompletedSuccessfully)
                    .Select(static t => t.Result).Where(static r => r is not null)
                    .ToDictionary(static r => r!.Deployment, static r => r!, StringComparer.Ordinal);
                var updatedFunctions = currentDeployments.Functions.AsValueEnumerable().Select(function =>
                    requestsByDeployment.TryGetValue(function.Deployment, out var request)
                        ? function with { Replicas = request.Replicas } : function).ToList();
                Interlocked.Exchange(ref _deployments, currentDeployments with { Functions = updatedFunctions });
            }
        }
    }

    private async Task<ReplicaRequest?> ApplyScaleAsync(ScalingDecision decision, ScaleConfig? configuration, string? session,
        bool wakeUp, ReplicaRequest request)
    {
        try
        {
            diagnostics?.Record(decision with { Application = "Sent" }, configuration, session);
            var result = await kubernetesService.ScaleAsync(request);
            if (result is not null && configuration?.Triggers.Count > 0)
                autoScaler.RecordAppliedDecision(decision.Function, new DateTimeOffset(_nowProvider()).ToUnixTimeSeconds(),
                    decision.CurrentReplicas, result.Replicas, wakeUp);
            diagnostics?.Record(decision with { Application = result is null ? "Failed" : "Accepted",
                AcceptedReplicas = result?.Replicas }, configuration, session);
            return result;
        }
        catch
        {
            diagnostics?.Record(decision with { Application = "Failed" }, configuration, session);
            throw;
        }
    }

    internal ScalingEnvironment CaptureSimulationEnvironment(DateTime nowUtc)
    {
        var deployments = Deployments;
        var ticks = deployments.Functions.ToDictionary(f => f.Deployment,
            f => historyHttpService.GetTicksLastCall(f.Deployment), StringComparer.Ordinal);
        return new(deployments, ticks, _isTurnOnByDefault, nowUtc, streamDemand?.Capture());
    }

    internal IReadOnlyList<ScalingSourceDiagnostic> SourceDiagnostics(DeploymentInformation function, long now)
        => (function.Scale?.Sources ?? []).Where(s => s is not null).Select(source =>
        {
            var resolved = ExternalMetricsSource.Resolve(function.Namespace, function.Deployment, function.Scale!, source.Name);
            var observation = resolved is null ? null : externalSources?.Get(resolved.Identity);
            int interval = function.Scale!.ScrapeIntervalMilliseconds ?? slimFaasOptions.Value.MetricsScraping.ScrapeIntervalMilliseconds;
            string state = resolved is null ? "Misconfigured" : observation?.State == ScalerState.Valid &&
                now - observation.LastSuccess >= interval * 3 / 1000.0 ? "Stale" : observation?.State.ToString() ?? "Unavailable";
            return new ScalingSourceDiagnostic(source.Name, state,
                observation?.LastSuccess > 0 ? observation.LastSuccess * 1000 : null, interval);
        }).ToArray();

    internal static ScalingFunctionContext CaptureContext(DeploymentsInformations deployments, DeploymentInformation function,
        DateTime nowUtc, IReadOnlyDictionary<string, long> ticks, long maximumTicks,
        IReadOnlySet<string> dependencyDemand, bool turnOnByDefault)
    {
        long http = ticks.GetValueOrDefault(function.Deployment);
        long effective = function.ReplicasStartAsSoonAsOneFunctionRetrieveARequest ? maximumTicks : http;
        if (turnOnByDefault && effective == 0) effective = nowUtc.Ticks;
        long? schedule = GetLastTicksFromSchedule(function, nowUtc);
        if (schedule > effective) effective = schedule.Value;
        foreach (var dependent in deployments.Functions.Where(f => f.DependsOn?.Contains(function.Deployment) == true))
            effective = Math.Max(effective, ticks.GetValueOrDefault(dependent.Deployment));
        var window = GetScheduleWindow(function, nowUtc);
        var scheduled = window?.MinActiveReplicas is not null || window?.MinIdleReplicas is not null
            ? function with { ReplicasAtStart = window.MinActiveReplicas ?? function.ReplicasAtStart,
                ReplicasMin = window.MinIdleReplicas ?? function.ReplicasMin }
            : function;
        return new(scheduled, nowUtc, http, schedule, effective,
            window?.Value ?? function.TimeoutSecondBeforeSetReplicasMin, dependencyDemand.Contains(function.Deployment),
            DependsOnReady(deployments, function, nowUtc), HasInfrastructurePodFailure(function)?.Reason);
    }

    internal static HashSet<string> GetExternalDependencyDemand(DeploymentsInformations deployments,
        IReadOnlyDictionary<string, ScalerEvaluation> evaluations, IReadOnlyDictionary<string, long>? streamDemand = null)
    {
        var functions = deployments.Functions.ToDictionary(f => f.Deployment, StringComparer.Ordinal);
        var demanded = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(evaluations.Where(e => e.Value.HasActiveExternalSignal).Select(e => e.Key)
            .Concat(streamDemand?.Where(item => item.Value > 0).Select(item => item.Key) ?? []));
        while (pending.TryPop(out var name))
        {
            if (!visited.Add(name) || !functions.TryGetValue(name, out var function)) continue;
            foreach (var dependency in function.DependsOn ?? [])
                if (functions.ContainsKey(dependency))
                {
                    demanded.Add(dependency);
                    pending.Push(dependency);
                }
        }
        return demanded;
    }

    private static ZonedDateTime LocalNow(DateTime nowUtc, string zoneId) =>
        Instant.FromDateTimeUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)).InZone(DateTimeZoneProviders.Tzdb[zoneId]);

    private static bool TryLocalTime(string text, out LocalTime time)
    {
        time = default;
        var span = text.AsSpan();
        int separator = span.IndexOf(':');
        if (separator < 0 ||
            !int.TryParse(span[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour) ||
            !int.TryParse(span[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute) ||
            hour is < 0 or > 23 || minute is < 0 or > 59)
            return false;
        time = new LocalTime(hour, minute);
        return true;
    }

    private static Instant Occurrence(LocalDate date, LocalTime time, DateTimeZone zone) =>
        date.At(time).InZoneLeniently(zone).ToInstant();

    public static long? GetLastTicksFromSchedule(DeploymentInformation deploymentInformation, DateTime nowUtc)
    {
        var schedule = deploymentInformation.Schedule;
        if (schedule?.Default?.WakeUp is not { Count: > 0 } times) return null;
        var local = LocalNow(nowUtc, schedule.TimeZoneID);
        Instant? latest = null;
        foreach (var text in times)
        {
            if (!TryLocalTime(text, out var time)) continue;
            var occurrence = Occurrence(local.Date, time, local.Zone);
            if (occurrence > local.ToInstant())
                occurrence = Occurrence(local.Date.PlusDays(-1), time, local.Zone);
            if (latest is null || occurrence > latest) latest = occurrence;
        }
        return latest?.ToDateTimeUtc().Ticks;
    }

    private static ScaleDownTimeout? GetScheduleWindow(DeploymentInformation function, DateTime nowUtc)
    {
        var schedule = function.Schedule;
        if (schedule?.Default?.ScaleDownTimeout is not { Count: > 0 } windows) return null;
        var local = LocalNow(nowUtc, schedule.TimeZoneID);
        ScaleDownTimeout? active = null, previous = null;
        Instant? latestToday = null, latestYesterday = null;
        int valid = 0;
        foreach (var window in windows)
        {
            if (!TryLocalTime(window.Time, out var time)) continue;
            valid++;
            var occurrence = Occurrence(local.Date, time, local.Zone);
            if (occurrence <= local.ToInstant())
            {
                if (latestToday is null || occurrence > latestToday)
                    (active, latestToday) = (window, occurrence);
            }
            else if (active is null && windows.Count > 1)
            {
                occurrence = Occurrence(local.Date.PlusDays(-1), time, local.Zone);
                if (latestYesterday is null || occurrence > latestYesterday)
                    (previous, latestYesterday) = (window, occurrence);
            }
        }
        // A lone checkpoint uses deployment defaults before its start each day.
        return active ?? (valid > 1 ? previous : null);
    }

    public static int GetTimeoutSecondBeforeSetReplicasMin(DeploymentInformation deploymentInformation, DateTime nowUtc)
        => GetScheduleWindow(deploymentInformation, nowUtc)?.Value ?? deploymentInformation.TimeoutSecondBeforeSetReplicasMin;

    private static bool DependsOnReady(
        DeploymentsInformations deployments,
        DeploymentInformation deploymentInformation, DateTime nowUtc)
    {
        if (deploymentInformation.DependsOn == null) return true;

        foreach (string dependOn in deploymentInformation.DependsOn)
        {
            if (DependencyReference.TryGetLocalProcessName(dependOn, out _))
            {
                if (!DependencyReference.IsLocalProcessReady(deployments, dependOn))
                    return false;
                continue;
            }

            if (deployments.Functions
                .Where(f => f.Deployment == dependOn)
                .Any(f => f.Pods.Count(p => p.Ready.HasValue && p.Ready.Value) <
                    (GetScheduleWindow(f, nowUtc)?.MinActiveReplicas ?? f.ReplicasAtStart)))
            {
                return false;
            }
        }
        return true;
    }

    private static (string Reason, string? Message)? HasInfrastructurePodFailure(DeploymentInformation deploymentInformation)
    {
        if (deploymentInformation.Pods.Count == 0)
        {
            return null;
        }

        foreach (var pod in deploymentInformation.Pods)
        {
            if (IsInfrastructureFailure(pod))
            {
                return (pod.StartFailureReason!, pod.StartFailureMessage);
            }
        }

        return null;
    }

}
