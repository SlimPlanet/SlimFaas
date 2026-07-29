using System.Collections.Concurrent;
using SlimFaas.Kubernetes;

namespace SlimFaas
{
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

        // Etat local du load-balancer (heuristique locale au process).
        public static ConcurrentDictionary<string, string> IpAddresses { get; } = new();
        private static ConcurrentDictionary<string, ConcurrentDictionary<string, int>> InFlightSyncByDeployment { get; } = new();
        private static ConcurrentDictionary<string, object> DeploymentLocks { get; } = new();

        public Proxy(IReplicasService replicasService, string functionName)
        {
            _replicasService = replicasService;
            _functionName = functionName;
        }

        private readonly Random _random = new();

        private static object GetDeploymentLock(string deployment)
            => DeploymentLocks.GetOrAdd(deployment, static _ => new object());

        private static ConcurrentDictionary<string, int> GetOrCreateInFlight(string deployment)
            => InFlightSyncByDeployment.GetOrAdd(deployment,
                static _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        private static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
        {
            return replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
        }

        public IList<int>? GetPorts()
        {
            var deploymentInformation = SearchFunction(_replicasService, _functionName);
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

            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            return FindReadyPod(deploymentInformation, target)?.Ports;
        }

        public string? ResolvePodIp(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            return FindReadyPod(deploymentInformation, target)?.Ip;
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
            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            if (deploymentInformation == null)
            {
                return "";
            }

            var deployment = deploymentInformation.Deployment;
            var lockObject = GetDeploymentLock(deployment);

            lock (lockObject)
            {
                var readyTargets = deploymentInformation.Pods
                    .Where(pod => pod.Ready == true)
                    .Select(RoutingTarget)
                    .ToList();

                if (readyTargets.Count == 0)
                {
                    return "";
                }

                var inFlight = GetOrCreateInFlight(deployment);
                var activeByTarget = inFlight.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value,
                    StringComparer.OrdinalIgnoreCase);

                var selected = SelectBestTarget(
                    deployment,
                    readyTargets,
                    int.MaxValue,
                    activeByTarget);
                if (string.IsNullOrWhiteSpace(selected))
                {
                    return "";
                }

                inFlight.AddOrUpdate(selected, 1, static (_, current) => current + 1);
                return selected;
            }
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

            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            var deployment = deploymentInformation?.Deployment ?? _functionName;
            var lockObject = GetDeploymentLock(deployment);

            lock (lockObject)
            {
                if (!InFlightSyncByDeployment.TryGetValue(deployment, out var inFlight))
                {
                    return;
                }

                if (!inFlight.TryGetValue(ip, out var current))
                {
                    return;
                }

                if (current <= 1)
                {
                    inFlight.TryRemove(ip, out _);
                }
                else
                {
                    inFlight[ip] = current - 1;
                }
            }
        }

        private string GetNextIPInternal(int maxPerPod, IReadOnlyCollection<string>? alreadyUsedIps)
        {
            var deploymentInformation = SearchFunction(_replicasService, _functionName);
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

        private string SelectBestTarget(
            string deployment,
            IList<string> readyTargets,
            int maxPerPod,
            IReadOnlyDictionary<string, int> activeByTarget)
        {
            if (readyTargets.Count == 0)
            {
                return "";
            }

            // Déterminer l'index de départ (round-robin)
            int startIndex;
            if (!IpAddresses.TryGetValue(deployment, out var lastIp)
                || string.IsNullOrWhiteSpace(lastIp))
            {
                startIndex = _random.Next(0, readyTargets.Count);
            }
            else
            {
                var currentIndex = readyTargets.IndexOf(lastIp);
                startIndex = currentIndex == -1
                    ? _random.Next(0, readyTargets.Count)
                    : (currentIndex + 1) % readyTargets.Count;
            }

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

                // Pénalité légère sur le dernier pod choisi pour éviter le "sticky"
                // lorsque plusieurs pods ont la même charge.
                double score = active;
                if (!string.IsNullOrWhiteSpace(lastIp)
                    && string.Equals(candidateIp, lastIp, StringComparison.OrdinalIgnoreCase))
                {
                    score += 0.25;
                }

                if (score < bestScore)
                {
                    bestIp = candidateIp;
                    bestScore = score;
                }
            }

            if (bestIp != null)
            {
                IpAddresses[deployment] = bestIp;
                return bestIp;
            }

            // Tous les pods sont saturés
            return "";
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
