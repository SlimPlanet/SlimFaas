using SlimFaas.Kubernetes;
using NodaTime;
using NodaTime.TimeZones;
using Microsoft.Extensions.Options;
using SlimFaas.Options;
using SlimFaas.Scaling;

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
    ExternalMetricsSourceStore? externalSources = null)
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
        var dependencyDemand = GetExternalDependencyDemand(currentDeployments, evaluations);
        diagnostics?.Retain(currentDeployments.Functions.Select(f => f.Deployment).ToHashSet(StringComparer.Ordinal));

        List<Task<ReplicaRequest?>> tasks = new();

        foreach (DeploymentInformation deploymentInformation in currentDeployments.Functions)
        {
            var context = CaptureContext(currentDeployments, deploymentInformation, nowUtc,
                ticksLastCall, maximumTicks, dependencyDemand, _isTurnOnByDefault);
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug("Time left without request for scale down {Deployment} is {TimeLeft}",
                    deploymentInformation.Deployment, TimeSpan.FromTicks(context.EffectiveActivityTicks)
                        + TimeSpan.FromSeconds(context.TimeoutSeconds) - TimeSpan.FromTicks(nowUtc.Ticks));
            int currentScale = deploymentInformation.Replicas;
            evaluations.TryGetValue(deploymentInformation.Deployment, out var evaluation);
            MetricsScalingDecision? metrics = null;
            if (ScalingDecisionCalculator.NeedsMetrics(context, evaluation))
                metrics = autoScaler.ComputeDecision(deploymentInformation, nowUnixSeconds, evaluation!, recordDecision: false);
            var decision = ScalingDecisionCalculator.Calculate(context, evaluation, metrics,
                SourceDiagnostics(deploymentInformation, nowUnixSeconds));
            if (decision.Reasons.Any(r => r.Code == "InfrastructureBlocked"))
            {
                var failure = HasInfrastructurePodFailure(deploymentInformation);
                logger.LogWarning("Skip scale-up for {Deployment} because a pod is blocked by Infrastructure Error: {PodFailureReason}: {PodFailureMessage}",
                    deploymentInformation.Deployment, failure?.Reason, failure?.Message);
            }
            int desiredReplicas = decision.Target;
            diagnostics?.Record(decision, deploymentInformation.Scale, diagnosticSession);

            if (desiredReplicas == currentScale)
            {
                continue;
            }

            logger.LogInformation("Scale {Deployment} from {CurrentScale} to {DesiredReplicas}",
                deploymentInformation.Deployment, currentScale, desiredReplicas);

            tasks.Add(ApplyScaleAsync(decision, deploymentInformation.Scale, diagnosticSession, new ReplicaRequest(
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
                var requestsByDeployment = tasks.Where(t => t.IsCompletedSuccessfully)
                    .Select(t => t.Result).Where(r => r is not null)
                    .ToDictionary(r => r!.Deployment, r => r!, StringComparer.Ordinal);
                var updatedFunctions = currentDeployments.Functions.Select(function =>
                    requestsByDeployment.TryGetValue(function.Deployment, out var request)
                        ? function with { Replicas = request.Replicas } : function).ToList();
                Interlocked.Exchange(ref _deployments, currentDeployments with { Functions = updatedFunctions });
            }
        }
    }

    private async Task<ReplicaRequest?> ApplyScaleAsync(ScalingDecision decision, ScaleConfig? configuration, string? session, ReplicaRequest request)
    {
        try
        {
            diagnostics?.Record(decision with { Application = "Sent" }, configuration, session);
            var result = await kubernetesService.ScaleAsync(request);
            if (result is not null && configuration?.Triggers.Count > 0)
                autoScaler.RecordAppliedDecision(decision.Function, new DateTimeOffset(_nowProvider()).ToUnixTimeSeconds(),
                    decision.CurrentReplicas, result.Replicas);
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
        return new(deployments, ticks, _isTurnOnByDefault, nowUtc);
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
        return new(function, nowUtc, http, schedule, effective,
            GetTimeoutSecondBeforeSetReplicasMin(function, nowUtc), dependencyDemand.Contains(function.Deployment),
            DependsOnReady(deployments, function), HasInfrastructurePodFailure(function)?.Reason);
    }

    internal static HashSet<string> GetExternalDependencyDemand(DeploymentsInformations deployments,
        IReadOnlyDictionary<string, ScalerEvaluation> evaluations)
    {
        var functions = deployments.Functions.ToDictionary(f => f.Deployment, StringComparer.Ordinal);
        var demanded = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(evaluations.Where(e => e.Value.HasActiveExternalSignal).Select(e => e.Key));
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

    record TimeToScaleDownTimeout(int Hours, int Minutes, int Value, DateTime DateTime);

    // TZDB data is immutable for the lifetime of the process: cache the ForId
    // resolution, otherwise called for every schedule entry on every tick.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeZone> TimeZoneCache =
        new(StringComparer.Ordinal);

    private static DateTime CreateDateTime(DateTime dateTime, int hours, int minutes, string timeZoneId)
    {
        LocalDateTime local = new(dateTime.Year, dateTime.Month, dateTime.Day, hours, minutes);
        DateTimeZone dateTimeZone = TimeZoneCache.GetOrAdd(
            timeZoneId,
            static id => TzdbDateTimeZoneSource.Default.ForId(id));
        ZonedDateTime zonedDateTime = local.InZoneLeniently(dateTimeZone);
        return zonedDateTime.ToDateTimeUtc();
    }

    public static long? GetLastTicksFromSchedule(DeploymentInformation deploymentInformation, DateTime nowUtc)
    {
        if (deploymentInformation.Schedule is not { Default: not null })
        {
            return null;
        }

        var dateTime = DateTime.MinValue;
        var dates = new List<DateTime>();

        foreach (var defaultSchedule in deploymentInformation.Schedule.Default.WakeUp)
        {
            var splits = defaultSchedule.Split(':');
            if (splits.Length != 2) continue;

            if (!int.TryParse(splits[0], out int hours) || !int.TryParse(splits[1], out int minutes))
            {
                continue;
            }

            var date = CreateDateTime(nowUtc, hours, minutes, deploymentInformation.Schedule.TimeZoneID);
            dates.Add(date);
        }

        foreach (var date in dates)
        {
            if (date <= nowUtc && date > dateTime)
            {
                dateTime = date;
            }
        }

        if (dateTime > DateTime.MinValue)
        {
            return dateTime.Ticks;
        }

        if (dateTime == DateTime.MinValue && dates.Count > 0)
        {
            dateTime = dates.OrderBy(d => d).Last();
            return dateTime.AddDays(-1).Ticks;
        }

        return null;
    }

    public static int GetTimeoutSecondBeforeSetReplicasMin(DeploymentInformation deploymentInformation, DateTime nowUtc)
    {
        if (deploymentInformation.Schedule is { Default: not null })
        {
            List<TimeToScaleDownTimeout> times = new();
            foreach (var defaultSchedule in deploymentInformation.Schedule.Default.ScaleDownTimeout)
            {
                var splits = defaultSchedule.Time.Split(':');
                if (splits.Length != 2) continue;
                if (!int.TryParse(splits[0], out int hours) || !int.TryParse(splits[1], out int minutes)) continue;

                var date = CreateDateTime(nowUtc, hours, minutes, deploymentInformation.Schedule.TimeZoneID);
                times.Add(new TimeToScaleDownTimeout(date.Hour, date.Minute, defaultSchedule.Value, date));
            }

            if (times.Count >= 2)
            {
                TimeToScaleDownTimeout? latestPastTime = null;
                foreach (var time in times)
                {
                    if (time.DateTime.Ticks < nowUtc.Ticks &&
                        (latestPastTime is null || time.DateTime.Ticks > latestPastTime.DateTime.Ticks))
                    {
                        latestPastTime = time;
                    }
                }

                if (latestPastTime is not null)
                {
                    return latestPastTime.Value;
                }

                TimeToScaleDownTimeout latestTime = times[0];
                for (int index = 1; index < times.Count; index++)
                {
                    if (times[index].DateTime.Ticks > latestTime.DateTime.Ticks)
                    {
                        latestTime = times[index];
                    }
                }

                return latestTime.Value;
            }
            else if (times.Count == 1)
            {
                var time = times.First();
                return (time.DateTime.Ticks < nowUtc.Ticks) ? time.Value : deploymentInformation.TimeoutSecondBeforeSetReplicasMin;
            }
        }

        return deploymentInformation.TimeoutSecondBeforeSetReplicasMin;
    }

    private static bool DependsOnReady(
        DeploymentsInformations deployments,
        DeploymentInformation deploymentInformation)
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
                .Any(f => f.Pods.Count(p => p.Ready.HasValue && p.Ready.Value) < f.ReplicasAtStart))
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
