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
    ILogger<ReplicasService> logger)
    : IReplicasService
{
    private readonly bool _isTurnOnByDefault = EnvironmentVariables.ReadBoolean(logger,
        EnvironmentVariables.PodScaledUpByDefaultWhenInfrastructureHasNeverCalled,
        EnvironmentVariables.PodScaledUpByDefaultWhenInfrastructureHasNeverCalledDefault);

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
        // Remplacement atomique de l'instance.
        Interlocked.Exchange(ref _deployments, deployments);
        return deployments;
    }

    public async Task CheckScaleAsync(string kubeNamespace)
    {
        var currentDeployments = _deployments;

        long maximumTicks = 0L;
        var ticksLastCall = new Dictionary<string, long>();
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
                tickLastCall = DateTime.UtcNow.Ticks;
            }

            var lastTicksFromSchedule = GetLastTicksFromSchedule(deploymentInformation, DateTime.UtcNow);
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

            var timeToWaitSeconds = TimeSpan.FromSeconds(GetTimeoutSecondBeforeSetReplicasMin(deploymentInformation, DateTime.UtcNow));
            bool timeElapsedWithoutRequest = (TimeSpan.FromTicks(tickLastCall) + timeToWaitSeconds) < TimeSpan.FromTicks(DateTime.UtcNow.Ticks);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                var time = (TimeSpan.FromTicks(tickLastCall) + timeToWaitSeconds) - TimeSpan.FromTicks(DateTime.UtcNow.Ticks);
                logger.LogDebug("Time left without request for scale down {Deployment} is {TimeElapsedWithoutRequest}",
                    deploymentInformation.Deployment, time);
            }
            int currentScale = deploymentInformation.Replicas;
            if (timeElapsedWithoutRequest)
            {
                if (currentScale == deploymentInformation.ReplicasMin)
                {
                    continue;
                }

                if (currentScale < deploymentInformation.ReplicasMin)
                {
                    logger.LogInformation("Scale up {Deployment} from {CurrentScale} to {ReplicaAtStart}",
                        deploymentInformation.Deployment, currentScale, deploymentInformation.ReplicasAtStart);
                }
                else
                {
                    logger.LogInformation("Scale down {Deployment} from {CurrentScale} to {ReplicasMin}",
                        deploymentInformation.Deployment, currentScale, deploymentInformation.ReplicasMin);
                }

                tasks.Add(kubernetesService.ScaleAsync(new ReplicaRequest(
                    Replicas: deploymentInformation.ReplicasMin,
                    Deployment: deploymentInformation.Deployment,
                    Namespace: kubeNamespace,
                    PodType: deploymentInformation.PodType
                )));
            }
            else if ((currentScale is 0 || currentScale < deploymentInformation.ReplicasMin) && DependsOnReady(deploymentInformation))
            {
                logger.LogInformation("Scale up {Deployment} from {CurrentScale} to {ReplicaAtStart}",
                    deploymentInformation.Deployment, currentScale, deploymentInformation.ReplicasAtStart);
                tasks.Add(kubernetesService.ScaleAsync(new ReplicaRequest(
                    Replicas: deploymentInformation.ReplicasAtStart,
                    Deployment: deploymentInformation.Deployment,
                    Namespace: kubeNamespace,
                    PodType: deploymentInformation.PodType
                )));
            }
        }

        if (tasks.Count > 0)
        {
            List<DeploymentInformation> updatedFunctions = new();
            ReplicaRequest?[] replicaRequests = await Task.WhenAll(tasks);
            foreach (DeploymentInformation function in currentDeployments.Functions)
            {
                ReplicaRequest? updatedFunction = replicaRequests.ToList().Find(t => t?.Deployment == function.Deployment);
                updatedFunctions.Add(function with { Replicas = updatedFunction?.Replicas ?? function.Replicas });
            }
            // Création d'une nouvelle instance en combinant les mises à jour.
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

                /*
                    Convert to ticks to prevent schedule elements, when moving to utc time, from taking precedence when they shoudln't.
                    For instance: 1am in French would become 11pm of the day before in utc hour.
                    This would make it take precedence over almost every other time.
                    Therefore, comparing only the total amount of minutes, as was done before, would not work.
                */
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
