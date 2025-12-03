using SlimFaas.Kubernetes;
using NodaTime;
using NodaTime.TimeZones;

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
    Func<DateTime>? nowProvider = null)
    : IReplicasService
{
    private readonly bool _isTurnOnByDefault = EnvironmentVariables.ReadBoolean(logger,
        EnvironmentVariables.PodScaledUpByDefaultWhenInfrastructureHasNeverCalled,
        EnvironmentVariables.PodScaledUpByDefaultWhenInfrastructureHasNeverCalledDefault);

    private readonly Func<DateTime> _nowProvider = nowProvider ?? (() => DateTime.UtcNow);

    // On part du principe que DeploymentsInformations est (quasi) immuable.
    private DeploymentsInformations _deployments = new(
        new List<DeploymentInformation>(),
        new SlimFaasDeploymentInformation(1, new List<PodInformation>()),
        new List<PodInformation>());

    // Lecture sans verrou puisque _deployments est remplacé atomiquement.
    public DeploymentsInformations Deployments =>
        // On retourne ici une copie si nécessaire pour éviter que le consommateur ne modifie la collection.
        new(
            _deployments.Functions.ToArray(),
            new SlimFaasDeploymentInformation(_deployments.SlimFaas?.Replicas ?? 1,
                                               _deployments.SlimFaas?.Pods ?? new List<PodInformation>()),
            new List<PodInformation>());

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

        List<Task<ReplicaRequest?>> tasks = new();

        foreach (DeploymentInformation deploymentInformation in currentDeployments.Functions)
        {
            long tickLastCall = deploymentInformation.ReplicasStartAsSoonAsOneFunctionRetrieveARequest
                ? maximumTicks
                : ticksLastCall[deploymentInformation.Deployment];

            if (_isTurnOnByDefault && tickLastCall == 0)
            {
                tickLastCall = nowUtc.Ticks;
            }

            var lastTicksFromSchedule = GetLastTicksFromSchedule(deploymentInformation, nowUtc);
            if (lastTicksFromSchedule.HasValue && lastTicksFromSchedule > tickLastCall)
            {
                tickLastCall = lastTicksFromSchedule.Value;
            }

            var allDependsOn = currentDeployments.Functions
                .Where(f => f.DependsOn != null && f.DependsOn.Contains(deploymentInformation.Deployment))
                .ToList();

            foreach (DeploymentInformation information in allDependsOn)
            {
                if (tickLastCall < ticksLastCall[information.Deployment])
                    tickLastCall = ticksLastCall[information.Deployment];
            }

            var timeoutSeconds = TimeSpan.FromSeconds(GetTimeoutSecondBeforeSetReplicasMin(deploymentInformation, nowUtc));
            bool timeElapsedWithoutRequest =
                (TimeSpan.FromTicks(tickLastCall) + timeoutSeconds) < TimeSpan.FromTicks(nowUtc.Ticks);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var timeLeft = (TimeSpan.FromTicks(tickLastCall) + timeoutSeconds) - TimeSpan.FromTicks(nowUtc.Ticks);
                logger.LogDebug("Time left without request for scale down {Deployment} is {TimeLeft}",
                    deploymentInformation.Deployment, timeLeft);
            }

            int currentScale = deploymentInformation.Replicas;
            int desiredReplicas = currentScale;

            // --- Calcul commun : désir des métriques (Prometheus), si activées ---
            int? desiredFromMetrics = null;
            if (deploymentInformation.Scale is not null && currentScale > 0)
            {
                desiredFromMetrics = autoScaler.ComputeDesiredReplicas(deploymentInformation, nowUnixSeconds);
            }

            // --- 1. SYSTÈME 0 -> N / N -> ReplicasMin (historique HTTP + schedule) ---
            if (timeElapsedWithoutRequest)
            {
                // HTTP/schedule disent : "on peut descendre à ReplicasMin"
                var replicasMin = deploymentInformation.ReplicasMin;

                // ⚠️ Cas particulier : ReplicasMin == 0
                // On n'autorise le passage à 0 que si Prometheus (si activé) est d'accord.
                if (desiredFromMetrics.HasValue)
                {
                    // HTTP voudrait 0 mais les métriques disent qu'il faut encore >= 1 pod
                    // => on reste "au chaud" avec au moins 1 pod, ou plus si Prometheus le demande.
                    desiredReplicas = Math.Max(replicasMin, desiredFromMetrics.Value);
                }
                else
                {
                    // Comportement historique : ramener à ReplicasMin (qui peut être > 0)
                    desiredReplicas = replicasMin;
                }
            }
            else if ((currentScale == 0 || currentScale < deploymentInformation.ReplicasMin)
                     && DependsOnReady(deploymentInformation))
            {
                // Sortie de 0 ou mise à niveau jusqu'à ReplicasAtStart
                desiredReplicas = deploymentInformation.ReplicasAtStart;
            }
            else if (desiredFromMetrics.HasValue)
            {
                // --- 2. SYSTÈME N -> M (AutoScaler Prometheus) --- .
                desiredReplicas = Math.Max(desiredFromMetrics.Value, deploymentInformation.ReplicasAtStart);
            }

            if (desiredReplicas == currentScale)
            {
                continue;
            }

            logger.LogInformation("Scale {Deployment} from {CurrentScale} to {DesiredReplicas}",
                deploymentInformation.Deployment, currentScale, desiredReplicas);

            tasks.Add(kubernetesService.ScaleAsync(new ReplicaRequest(
                Replicas: desiredReplicas,
                Deployment: deploymentInformation.Deployment,
                Namespace: kubeNamespace,
                PodType: deploymentInformation.PodType
            )));
        }

        if (tasks.Count > 0)
        {
            List<DeploymentInformation> updatedFunctions = new();
            ReplicaRequest?[] replicaRequests = await Task.WhenAll(tasks);
            var requestsByDeployment = replicaRequests
                .Where(r => r is not null)
                .ToDictionary(r => r!.Deployment, r => r!, StringComparer.Ordinal);

            foreach (DeploymentInformation function in currentDeployments.Functions)
            {
                if (requestsByDeployment.TryGetValue(function.Deployment, out var updatedRequest))
                {
                    updatedFunctions.Add(function with { Replicas = updatedRequest.Replicas });
                }
                else
                {
                    updatedFunctions.Add(function);
                }
            }

            var updatedDeployments = currentDeployments with { Functions = updatedFunctions };
            Interlocked.Exchange(ref _deployments, updatedDeployments);
        }
    }

    record TimeToScaleDownTimeout(int Hours, int Minutes, int Value, DateTime DateTime);

    private static DateTime CreateDateTime(DateTime dateTime, int hours, int minutes, string timeZoneId)
    {
        TzdbDateTimeZoneSource source = TzdbDateTimeZoneSource.Default;
        LocalDateTime local = new(dateTime.Year, dateTime.Month, dateTime.Day, hours, minutes);
        DateTimeZone dateTimeZone = source.ForId(timeZoneId);
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
                var orderedTimes = times
                    .Where(t => t.DateTime.Ticks < nowUtc.Ticks)
                    .OrderBy(t => t.DateTime.Ticks)
                    .ToList();
                if (orderedTimes.Count >= 1)
                {
                    return orderedTimes[^1].Value;
                }
                return times.OrderBy(t => t.DateTime.Ticks).Last().Value;
            }
            else if (times.Count == 1)
            {
                var time = times.First();
                return (time.DateTime.Ticks < nowUtc.Ticks) ? time.Value : deploymentInformation.TimeoutSecondBeforeSetReplicasMin;
            }
        }

        return deploymentInformation.TimeoutSecondBeforeSetReplicasMin;
    }

    private bool DependsOnReady(DeploymentInformation deploymentInformation)
    {
        if (deploymentInformation.DependsOn == null) return true;

        foreach (string dependOn in deploymentInformation.DependsOn)
        {
            if (Deployments.Functions
                .Where(f => f.Deployment == dependOn)
                .Any(f => f.Pods.Count(p => p.Ready.HasValue && p.Ready.Value) < f.ReplicasAtStart))
            {
                return false;
            }
        }
        return true;
    }
}
