using SlimFaas.Kubernetes;

namespace SlimFaas.WebSocket;

/// <summary>
/// Fournit une vue des fonctions WebSocket sous forme de <see cref="DeploymentInformation"/> virtuelles,
/// permettant au reste de SlimFaas (ScaleWorker, EventEndpoints, etc.) de les traiter
/// comme des déploiements normaux.
/// </summary>
public interface IWebSocketFunctionRepository
{
    /// <summary>
    /// Retourne les <see cref="DeploymentInformation"/> synthétiques pour chaque
    /// nom de fonction dont au moins un client WebSocket est connecté.
    /// </summary>
    IReadOnlyList<DeploymentInformation> GetVirtualDeployments();
}

public class WebSocketFunctionRepository : IWebSocketFunctionRepository
{
    private readonly WebSocketConnectionRegistry _registry;

    public WebSocketFunctionRepository(WebSocketConnectionRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<DeploymentInformation> GetVirtualDeployments()
    {
        var result = new List<DeploymentInformation>();

        foreach (var functionName in _registry.GetRegisteredFunctionNames())
        {
            var config = _registry.GetConfiguration(functionName);
            if (config == null) continue;

            var connections = _registry.GetConnections(functionName);
            if (connections.Count == 0) continue;

            // Chaque connexion WebSocket active = un "pod" virtuel
            var pods = connections
                .Where(c => c.IsAlive)
                .Select(c => new PodInformation(
                    Name: $"ws-{c.ConnectionId[..8]}",
                    Started: true,
                    Ready: true,
                    Ip: c.ConnectionId,          // L'IP virtuelle = connectionId
                    DeploymentName: functionName,
                    Ports: null))
                .ToList();

            FunctionVisibility visibility = config.DefaultVisibility
                .Equals("Private", StringComparison.OrdinalIgnoreCase)
                ? FunctionVisibility.Private
                : FunctionVisibility.Public;

            FunctionTrust trust = config.DefaultTrust
                .Equals("Untrusted", StringComparison.OrdinalIgnoreCase)
                ? FunctionTrust.Untrusted
                : FunctionTrust.Trusted;

            var subscribeEvents = config.SubscribeEvents
                .Select(e => new SubscribeEvent(e, visibility))
                .ToList();

            var pathsVisibility = config.PathsStartWithVisibility
                .Select(kvp => new PathVisibility(
                    kvp.Key,
                    kvp.Value.Equals("Private", StringComparison.OrdinalIgnoreCase)
                        ? FunctionVisibility.Private
                        : FunctionVisibility.Public))
                .ToList();

            var info = new DeploymentInformation(
                Deployment: functionName,
                Namespace: "websocket-virtual",
                Pods: pods,
                Configuration: new SlimFaasConfiguration(),
                Replicas: pods.Count,
                ReplicasAtStart: pods.Count,
                ReplicasMin: 0,
                TimeoutSecondBeforeSetReplicasMin: 0,
                NumberParallelRequest: config.NumberParallelRequest,
                ReplicasStartAsSoonAsOneFunctionRetrieveARequest: config.ReplicasStartAsSoonAsOneFunctionRetrieveARequest,
                PodType: PodType.Deployment,
                DependsOn: config.DependsOn,
                SubscribeEvents: subscribeEvents,
                Visibility: visibility,
                PathsStartWithVisibility: pathsVisibility,
                ResourceVersion: "ws",
                EndpointReady: true,
                Trust: trust,
                NumberParallelRequestPerPod: config.NumberParallelRequestPerPod
            );

            result.Add(info);
        }

        return result;
    }
}

