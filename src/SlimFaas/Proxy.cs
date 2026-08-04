using System.Collections.Concurrent;
using SlimFaas.Kubernetes;

namespace SlimFaas
{
    internal readonly record struct SyncTargetReservation(
        string RoutingTarget,
        string Ip,
        IList<int>? Ports,
        string? EndpointUrl);

    public interface IProxy
    {
        /// <summary>
        /// Sélection round-robin simple, sans contrainte de charge per-pod.
        /// Utilisée par le proxy synchrone HTTP.
        /// </summary>
        string GetNextIP();

        /// <summary>
        /// Sélection "least-connections" + round-robin tie-breaker, en respectant
        /// <paramref name="maxPerPod"/>. La charge actuelle de chaque pod est
        /// dérivée de <paramref name="alreadyUsedIps"/> (par ex. les IPs réservées
        /// par les éléments "Running" actuellement présents en base).
        /// Retourne "" si tous les pods sont saturés ou si aucun pod n'est ready.
        /// </summary>
        string GetNextIP(int maxPerPod, IReadOnlyCollection<string> alreadyUsedIps);

        IList<int>? GetPorts();
        IList<int>? GetPorts(string? target);

        /// <summary>
        /// Résout l'identité de routage sélectionnée vers l'adresse réseau réelle du pod.
        /// Par défaut, l'identité est déjà une IP (cas Kubernetes).
        /// </summary>
        string? ResolvePodIp(string? target) => target;

        /// <summary>
        /// Résout une URL de base explicite pour le pod sélectionné.
        /// Elle est absente pour les pods Kubernetes classiques.
        /// </summary>
        string? ResolvePodEndpointUrl(string? target) => null;

        /// <summary>
        /// Réserve <paramref name="count"/> IPs en simulant l'ajout progressif des
        /// IPs choisies à <paramref name="alreadyUsedIps"/>. Aucune mutation d'état
        /// global n'est effectuée : le caller est responsable du suivi (via la DB).
        /// </summary>
        IList<string> ReserveNextIPs(int maxPerPod, int count, IReadOnlyCollection<string> alreadyUsedIps);

        /// <summary>
        /// Réserve une IP pour un appel synchrone en tenant compte des requêtes locales en cours.
        /// Le caller doit appeler <see cref="ReleaseSyncIP"/> lorsque la réponse a été entièrement consommée.
        /// </summary>
        string AcquireNextIPForSync();

        /// <summary>
        /// Libère une IP précédemment réservée par <see cref="AcquireNextIPForSync"/>.
        /// </summary>
        void ReleaseSyncIP(string? ip);
    }

    public class Proxy : IProxy
    {
        private readonly IReplicasService _replicasService;
        private readonly string _functionName;
        private readonly DeploymentInformation? _deploymentSnapshot;

        // Etat local du load-balancer (heuristique locale au process).
        public static ConcurrentDictionary<string, string> IpAddresses { get; } = new();
        private static ConcurrentDictionary<string, ConcurrentDictionary<string, int>> InFlightSyncByDeployment { get; } = new();

        public Proxy(IReplicasService replicasService, string functionName)
            : this(replicasService, functionName, null)
        {
        }

        internal Proxy(
            IReplicasService replicasService,
            string functionName,
            DeploymentInformation? deploymentSnapshot)
        {
            _replicasService = replicasService;
            _functionName = functionName;
            _deploymentSnapshot = deploymentSnapshot;
        }

        private static ConcurrentDictionary<string, int> GetOrCreateInFlight(string deployment)
            => InFlightSyncByDeployment.GetOrAdd(deployment,
                static _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        private static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
        {
            return replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
        }

        private DeploymentInformation? GetDeployment() =>
            _deploymentSnapshot ?? SearchFunction(_replicasService, _functionName);

        public IList<int>? GetPorts()
        {
            var deploymentInformation = GetDeployment();
            return deploymentInformation?.Pods
                .Where(pod => pod.Ready == true)
                .Select(pod => pod.Ports)
                .FirstOrDefault();
        }

        public IList<int>? GetPorts(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return GetPorts();
            }

            var deploymentInformation = GetDeployment();
            return FindReadyPod(deploymentInformation, target)?.Ports;
        }

        public string? ResolvePodIp(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var deploymentInformation = GetDeployment();
            return FindReadyPod(deploymentInformation, target)?.Ip;
        }

        public string? ResolvePodEndpointUrl(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var deploymentInformation = GetDeployment();
            return FindReadyPod(deploymentInformation, target)?.EndpointUrl;
        }

        public IList<string> ReserveNextIPs(int maxPerPod, int count, IReadOnlyCollection<string> alreadyUsedIps)
        {
            var reserved = new List<string>(Math.Max(0, count));
            // Copie mutable que l'on enrichit au fur et à mesure pour simuler la réservation.
            var working = new List<string>(alreadyUsedIps);

            for (var i = 0; i < count; i++)
            {
                var ip = GetNextIP(maxPerPod, working);
                if (string.IsNullOrWhiteSpace(ip))
                {
                    break;
                }

                reserved.Add(ip);
                working.Add(ip);
            }

            return reserved;
        }

        public string GetNextIP() => GetNextIPInternal(int.MaxValue, null);

        public string GetNextIP(int maxPerPod, IReadOnlyCollection<string> alreadyUsedIps)
            => GetNextIPInternal(maxPerPod, alreadyUsedIps);

        /// <summary>
        /// Réserve une IP pour un appel sync en incrémentant un compteur local in-flight.
        /// Le caller doit appeler <see cref="ReleaseSyncIP"/> dans un finally.
        /// </summary>
        public string AcquireNextIPForSync()
        {
            return TryAcquireNextTargetForSync(out SyncTargetReservation reservation)
                ? reservation.RoutingTarget
                : "";
        }

        internal bool TryAcquireNextTargetForSync(out SyncTargetReservation reservation)
        {
            var deploymentInformation = GetDeployment();
            if (deploymentInformation is null)
            {
                reservation = default;
                return false;
            }

            var deployment = deploymentInformation.Deployment;
            if (deploymentInformation.Pods.Count == 1 &&
                deploymentInformation.Pods[0].Ready == true)
            {
                PodInformation onlyPod = deploymentInformation.Pods[0];
                string onlyTarget = RoutingTarget(onlyPod);
                if (string.IsNullOrWhiteSpace(onlyTarget))
                {
                    reservation = default;
                    return false;
                }

                GetOrCreateInFlight(deployment)
                    .AddOrUpdate(onlyTarget, 1, static (_, current) => current + 1);
                reservation = new SyncTargetReservation(
                    onlyTarget,
                    onlyPod.Ip,
                    onlyPod.Ports,
                    onlyPod.EndpointUrl);
                return true;
            }

            PodInformation[] readyPods = deploymentInformation.Pods
                .Where(pod => pod.Ready == true)
                .ToArray();

            if (readyPods.Length == 0)
            {
                reservation = default;
                return false;
            }

            string[] readyTargets = readyPods.Select(RoutingTarget).ToArray();
            var inFlight = GetOrCreateInFlight(deployment);
            string selected = SelectBestTarget(
                deployment,
                readyTargets,
                int.MaxValue,
                inFlight);
            if (string.IsNullOrWhiteSpace(selected))
            {
                reservation = default;
                return false;
            }

            inFlight.AddOrUpdate(selected, 1, static (_, current) => current + 1);
            PodInformation selectedPod = readyPods.First(pod =>
                string.Equals(RoutingTarget(pod), selected, StringComparison.OrdinalIgnoreCase));
            reservation = new SyncTargetReservation(
                selected,
                selectedPod.Ip,
                selectedPod.Ports,
                selectedPod.EndpointUrl);
            return true;
        }

        /// <summary>
        /// Libère l'IP précédemment réservée par <see cref="AcquireNextIPForSync"/>.
        /// </summary>
        public void ReleaseSyncIP(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                return;
            }

            var deployment = GetDeployment()?.Deployment ?? _functionName;
            if (!InFlightSyncByDeployment.TryGetValue(deployment, out var inFlight))
            {
                return;
            }

            inFlight.AddOrUpdate(ip, 0, static (_, current) => current > 0 ? current - 1 : 0);
        }

        private string GetNextIPInternal(int maxPerPod, IReadOnlyCollection<string>? alreadyUsedIps)
        {
            var deploymentInformation = GetDeployment();
            if (deploymentInformation == null)
            {
                return "";
            }

            var readyTargets = deploymentInformation.Pods
                .Where(pod => pod.Ready == true)
                .Select(RoutingTarget)
                .ToList();

            if (readyTargets.Count == 0)
            {
                return "";
            }

            // Comptage des requêtes actives par pod, dérivé directement de l'état fourni
            // (ex: IPs réservées par les éléments "Running" en DB).
            var activeByTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (alreadyUsedIps != null)
            {
                foreach (var target in alreadyUsedIps)
                {
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        continue;
                    }
                    activeByTarget[target] =
                        activeByTarget.TryGetValue(target, out var count) ? count + 1 : 1;
                }
            }

            return SelectBestTarget(
                deploymentInformation.Deployment,
                readyTargets,
                maxPerPod,
                activeByTarget);
        }

        private static string SelectBestTarget(
            string deployment,
            IList<string> readyTargets,
            int maxPerPod,
            IReadOnlyDictionary<string, int> activeByTarget)
        {
            if (readyTargets.Count == 0)
            {
                return "";
            }

            int startIndex = AdvanceRoundRobin(deployment, readyTargets);

            // Stratégie "least-connections" : parmi les pods non saturés, choisir
            // celui qui a le moins de requêtes actives. En cas d'égalité,
            // le premier rencontré dans l'ordre round-robin gagne.
            string? bestIp = null;
            double bestScore = double.MaxValue;
            for (int i = 0; i < readyTargets.Count; i++)
            {
                int index = (startIndex + i) % readyTargets.Count;
                string candidateIp = readyTargets[index];
                int active = activeByTarget.TryGetValue(candidateIp, out var count) ? count : 0;

                if (active >= maxPerPod)
                {
                    continue;
                }

                double score = active;
                if (score < bestScore)
                {
                    bestIp = candidateIp;
                    bestScore = score;
                }
            }

            if (bestIp != null)
            {
                return bestIp;
            }

            // Tous les pods sont saturés
            return "";
        }

        private static int AdvanceRoundRobin(string deployment, IList<string> readyTargets)
        {
            while (true)
            {
                if (!IpAddresses.TryGetValue(deployment, out string? previous) ||
                    string.IsNullOrWhiteSpace(previous))
                {
                    string initial = readyTargets[Random.Shared.Next(readyTargets.Count)];
                    if (IpAddresses.TryAdd(deployment, initial))
                    {
                        return readyTargets.IndexOf(initial);
                    }

                    continue;
                }

                int previousIndex = readyTargets.IndexOf(previous);
                int nextIndex = previousIndex < 0 ? 0 : (previousIndex + 1) % readyTargets.Count;
                string next = readyTargets[nextIndex];
                if (IpAddresses.TryUpdate(deployment, next, previous))
                {
                    return nextIndex;
                }
            }
        }

        private static string RoutingTarget(PodInformation pod)
            => string.IsNullOrWhiteSpace(pod.RoutingKey) ? pod.Ip : pod.RoutingKey;

        private static PodInformation? FindReadyPod(
            DeploymentInformation? deploymentInformation,
            string target)
        {
            if (deploymentInformation is null)
            {
                return null;
            }

            return deploymentInformation.Pods.FirstOrDefault(pod =>
                       pod.Ready == true &&
                       !string.IsNullOrWhiteSpace(pod.RoutingKey) &&
                       string.Equals(
                           pod.RoutingKey,
                           target,
                           StringComparison.OrdinalIgnoreCase))
                   ?? deploymentInformation.Pods.FirstOrDefault(pod =>
                       pod.Ready == true &&
                       string.Equals(
                           pod.Ip,
                           target,
                           StringComparison.OrdinalIgnoreCase));
        }
    }
}
