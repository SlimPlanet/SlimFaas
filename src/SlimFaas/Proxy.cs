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
        IList<int>? GetPorts(string? ip);

        /// <summary>
        /// Réserve <paramref name="count"/> IPs en simulant l'ajout progressif des
        /// IPs choisies à <paramref name="alreadyUsedIps"/>. Aucune mutation d'état
        /// global n'est effectuée : le caller est responsable du suivi (via la DB).
        /// </summary>
        IList<string> ReserveNextIPs(int maxPerPod, int count, IReadOnlyCollection<string> alreadyUsedIps);
    }

    public class Proxy : IProxy
    {
        private readonly IReplicasService _replicasService;
        private readonly string _functionName;

        // Seul état conservé : indice round-robin par déploiement (pure heuristique,
        // pas de correctness impact si désynchronisé entre instances).
        public static ConcurrentDictionary<string, string> IpAddresses { get; } = new();

        public Proxy(IReplicasService replicasService, string functionName)
        {
            _replicasService = replicasService;
            _functionName = functionName;
        }

        private readonly Random _random = new();

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

        public IList<int>? GetPorts(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                return GetPorts();
            }

            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            return deploymentInformation?.Pods
                .FirstOrDefault(pod => pod.Ready == true && string.Equals(pod.Ip, ip, StringComparison.OrdinalIgnoreCase))
                ?.Ports;
        }

        public IList<string> ReserveNextIPs(int maxPerPod, int count, IReadOnlyCollection<string> alreadyUsedIps)
        {
            var reserved = new List<string>(Math.Max(0, count));
            // Copie mutable que l'on enrichit au fur et à mesure pour simuler la réservation.
            var working = new List<string>(alreadyUsedIps ?? Array.Empty<string>());

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

        private string GetNextIPInternal(int maxPerPod, IReadOnlyCollection<string>? alreadyUsedIps)
        {
            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            if (deploymentInformation == null)
            {
                return "";
            }

            var readyPodsIps = deploymentInformation.Pods
                .Where(pod => pod.Ready == true)
                .Select(pod => pod.Ip)
                .ToList();

            if (readyPodsIps.Count == 0)
            {
                return "";
            }

            // Comptage des requêtes actives par pod, dérivé directement de l'état fourni
            // (ex: IPs réservées par les éléments "Running" en DB).
            var activeByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (alreadyUsedIps != null)
            {
                foreach (var ip in alreadyUsedIps)
                {
                    if (string.IsNullOrWhiteSpace(ip))
                    {
                        continue;
                    }
                    activeByIp[ip] = activeByIp.TryGetValue(ip, out var c) ? c + 1 : 1;
                }
            }

            // Déterminer l'index de départ (round-robin)
            int startIndex;
            if (!IpAddresses.TryGetValue(deploymentInformation.Deployment, out var lastIp)
                || string.IsNullOrWhiteSpace(lastIp))
            {
                startIndex = _random.Next(0, readyPodsIps.Count);
            }
            else
            {
                var currentIndex = readyPodsIps.IndexOf(lastIp);
                startIndex = currentIndex == -1
                    ? _random.Next(0, readyPodsIps.Count)
                    : (currentIndex + 1) % readyPodsIps.Count;
            }

            // Stratégie "least-connections" : parmi les pods non saturés, choisir
            // celui qui a le moins de requêtes actives. En cas d'égalité,
            // le premier rencontré dans l'ordre round-robin gagne.
            string? bestIp = null;
            int bestActive = int.MaxValue;
            for (int i = 0; i < readyPodsIps.Count; i++)
            {
                int index = (startIndex + i) % readyPodsIps.Count;
                string candidateIp = readyPodsIps[index];
                int active = activeByIp.TryGetValue(candidateIp, out var c) ? c : 0;

                if (active >= maxPerPod)
                {
                    continue;
                }

                if (active < bestActive)
                {
                    bestIp = candidateIp;
                    bestActive = active;
                }
            }

            if (bestIp != null)
            {
                IpAddresses[deploymentInformation.Deployment] = bestIp;
                return bestIp;
            }

            // Tous les pods sont saturés
            return "";
        }
    }
}
