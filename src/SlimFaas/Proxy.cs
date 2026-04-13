using System.Collections.Concurrent;
using SlimFaas.Kubernetes;

namespace SlimFaas
{
    public interface IProxy
    {
        string GetNextIP();
        string GetNextIP(int maxPerPod);
        IList<int>? GetPorts();
        IList<int>? GetPorts(string? ip);
        IList<string> ReserveNextIPs(int maxPerPod, int count);
        void ReleaseReservedIPs(IList<string> ips);
        bool BindElementToIp(string elementId, string ip);
        bool ReleaseElementReservation(string elementId, out string ip);
        void IncrementActiveRequests(string ip);
        void DecrementActiveRequests(string ip);
    }
    public class Proxy : IProxy
    {
        private readonly IReplicasService _replicasService;
        private readonly string _functionName;

        // Key: Nom du déploiement, Value: Dernière IP utilisée pour ce déploiement
        public static ConcurrentDictionary<string, string> IpAddresses { get; } = new();

        // Key: IP du pod, Value: nombre de requêtes actives sur ce pod
        public static ConcurrentDictionary<string, int> ActiveRequestsPerPod { get; } = new();

        // Key: IP du pod, Value: timestamp (UTC ticks) de la dernière sélection
        public static ConcurrentDictionary<string, long> LastRequestTicksPerPod { get; } = new();

        // Key: ElementId, Value: IP du pod réservé
        public static ConcurrentDictionary<string, string> ElementToPodIp { get; } = new();

        public Proxy(IReplicasService replicasService, string functionName)
        {
            _replicasService = replicasService;
            _functionName = functionName;
        }
        private readonly Random _random = new();

        private static DeploymentInformation? SearchFunction(IReplicasService replicasService, string functionName)
        {
            DeploymentInformation? function =
                replicasService.Deployments.Functions.FirstOrDefault(f => f.Deployment == functionName);
            return function;
        }

        public IList<int>? GetPorts()
        {
            var deploymentInformation = SearchFunction(_replicasService, _functionName);
            var readyPodsIps = deploymentInformation?.Pods
                .Where(pod => pod.Ready == true)
                .Select(pod => pod.Ports)
                .FirstOrDefault();

            return readyPodsIps;
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

        public void IncrementActiveRequests(string ip)
        {
            ActiveRequestsPerPod.AddOrUpdate(ip, 1, (_, count) => count + 1);
        }

        public void DecrementActiveRequests(string ip)
        {
            ActiveRequestsPerPod.AddOrUpdate(ip, 0, (_, count) => Math.Max(0, count - 1));
        }

        public IList<string> ReserveNextIPs(int maxPerPod, int count)
        {
            var reserved = new List<string>(Math.Max(0, count));
            for (var i = 0; i < count; i++)
            {
                var ip = GetNextIP(maxPerPod);
                if (string.IsNullOrWhiteSpace(ip))
                {
                    break;
                }

                IncrementActiveRequests(ip);
                reserved.Add(ip);
            }

            return reserved;
        }

        public void ReleaseReservedIPs(IList<string> ips)
        {
            if (ips.Count == 0)
            {
                return;
            }

            foreach (var ip in ips)
            {
                if (string.IsNullOrWhiteSpace(ip))
                {
                    continue;
                }

                DecrementActiveRequests(ip);
            }
        }

        public bool BindElementToIp(string elementId, string ip)
        {
            if (string.IsNullOrWhiteSpace(elementId) || string.IsNullOrWhiteSpace(ip))
            {
                return false;
            }

            ElementToPodIp[elementId] = ip;
            return true;
        }

        public bool ReleaseElementReservation(string elementId, out string ip)
        {
            if (ElementToPodIp.TryRemove(elementId, out ip!))
            {
                DecrementActiveRequests(ip);
                return true;
            }

            ip = string.Empty;
            return false;
        }

        /// <summary>
        /// Sélectionne le prochain pod en round-robin sans limite per-pod.
        /// </summary>
        public string GetNextIP() => GetNextIP(int.MaxValue);

        /// <summary>
        /// Sélectionne le prochain pod en utilisant une stratégie "least-connections"
        /// avec round-robin comme tie-breaker, en respectant la limite
        /// <paramref name="maxPerPod"/> de requêtes actives par pod.
        /// Retourne "" si tous les pods sont saturés ou aucun pod n'est ready.
        /// </summary>
        public string GetNextIP(int maxPerPod)
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

            if (!readyPodsIps.Any())
            {
                return "";
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

            // Stratégie "least-connections" : parmi les pods non saturés,
            // choisir celui qui a le moins de requêtes actives.
            // En cas d'égalité, le premier rencontré dans l'ordre round-robin gagne.
            string? bestIp = null;
            int bestActive = int.MaxValue;
            long bestLastRequestTick = long.MaxValue;
            for (int i = 0; i < readyPodsIps.Count; i++)
            {
                int index = (startIndex + i) % readyPodsIps.Count;
                string candidateIp = readyPodsIps[index];
                int active = ActiveRequestsPerPod.GetValueOrDefault(candidateIp, 0);

                if (active >= maxPerPod)
                {
                    continue;
                }

                // Un pod jamais sélectionné est prioritaire (tick=0) pour favoriser la rotation.
                long lastTick = LastRequestTicksPerPod.GetValueOrDefault(candidateIp, 0);

                bool isBetter = active < bestActive
                                || (active == bestActive && lastTick < bestLastRequestTick);

                if (!isBetter)
                {
                    continue;
                }

                bestIp = candidateIp;
                bestActive = active;
                bestLastRequestTick = lastTick;
            }

            if (bestIp != null)
            {
                IpAddresses[deploymentInformation.Deployment] = bestIp;
                LastRequestTicksPerPod[bestIp] = DateTime.UtcNow.Ticks;
                return bestIp;
            }

            // Tous les pods sont saturés — retourner "" pour signaler qu'il faut attendre
            return "";
        }
    }
}
