using System.Collections.Concurrent;
using SlimFaas.Kubernetes;

namespace SlimFaas
{
    public class Proxy
    {
        private readonly IReplicasService _replicasService;
        private readonly string _functionName;

        // Key: Nom du déploiement, Value: Dernière IP utilisée pour ce déploiement
        public static ConcurrentDictionary<string, string> IpAddresses = new();

        public Proxy(IReplicasService replicasService, string functionName)
        {
            _replicasService = replicasService;
            _functionName = functionName;
        }
        private readonly Random _random = new Random();

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

        public string GetNextIP()
        {
            var deploymentInformation = SearchFunction(_replicasService, _functionName);

            if (deploymentInformation == null)
            {
                return "";
            }

            // On récupère toutes les IP des pods qui sont en état "Ready"
            var readyPodsIps = deploymentInformation.Pods
                .Where(pod => pod.Ready == true)
                .Select(pod => pod.Ip)
                .ToList();

            // Si aucun pod n'est en état "Ready", on peut décider de retourner une chaîne vide ou lever une exception
            if (!readyPodsIps.Any())
            {
                return ""; // Ou lever une exception, selon le besoin
            }

            // On essaie de récupérer la dernière IP utilisée pour ce déploiement
            if (!IpAddresses.TryGetValue(deploymentInformation.Deployment, out var lastIp)
                || string.IsNullOrWhiteSpace(lastIp))
            {
                // Si aucune IP n'existe pour ce déploiement, on en sélectionne une au hasard
                var randomIndex = _random.Next(0, readyPodsIps.Count);
                var randomIp = readyPodsIps[randomIndex];

                // On sauvegarde cette IP comme dernière IP
                IpAddresses[deploymentInformation.Deployment] = randomIp;
                return randomIp;
            }

            // Sinon, on cherche la position de lastIp dans la liste des pods prêts
            var currentIndex = readyPodsIps.IndexOf(lastIp);

            // Si la dernière IP n'était pas dans la liste (par ex. si le pod a été supprimé),
            // on prend un pod prêt au hasard.
            if (currentIndex == -1)
            {
                var randomIndex = _random.Next(0, readyPodsIps.Count);
                var randomIp = readyPodsIps[randomIndex];

                IpAddresses[deploymentInformation.Deployment] = randomIp;
                return randomIp;
            }

            // On calcule l'index du "pod suivant" dans la liste
            var nextIndex = (currentIndex + 1) % readyPodsIps.Count;
            var nextIp = readyPodsIps[nextIndex];

            // On met à jour la dernière IP dans le dictionnaire
            IpAddresses[deploymentInformation.Deployment] = nextIp;
            return nextIp;
        }
    }
}
