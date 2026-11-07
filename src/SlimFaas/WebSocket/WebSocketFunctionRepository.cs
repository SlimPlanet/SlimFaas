using SlimFaas.Kubernetes;
using System.Text.Json;
using ZLinq;

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
    private readonly IWebSocketClusterDirectory? _directory;
    private readonly object _cacheLock = new();
    private IReadOnlyList<SlimData.WebSocketDirectoryConnection>? _connectionsSnapshot;
    private IReadOnlyList<DeploymentInformation> _globalSnapshot = [];

    public WebSocketFunctionRepository(WebSocketConnectionRegistry registry, IWebSocketClusterDirectory? directory = null)
    {
        _registry = registry;
        _directory = directory;
    }

    public IReadOnlyList<DeploymentInformation> GetVirtualDeployments()
    {
        if (_directory is not null)
        {
            var connections = _directory.Connections;
            lock (_cacheLock)
            {
                if (ReferenceEquals(connections, _connectionsSnapshot)) return _globalSnapshot;
                var result = new List<DeploymentInformation>();
                foreach (var group in connections.GroupBy(connection => connection.Function, StringComparer.OrdinalIgnoreCase))
                {
                    var config = JsonSerializer.Deserialize(group.First().Configuration, AppJsonContext.Default.WebSocketFunctionConfiguration)
                        ?? throw new InvalidDataException("Missing WebSocket directory configuration.");
                    WebSocketConnectionRegistry.ParseConfiguration(group.Key, config);
                    result.Add(CreateDeployment(group.Key, config, group.AsValueEnumerable().Select(static connection => connection.Id)));
                }
                _connectionsSnapshot = connections;
                _globalSnapshot = result;
                return result;
            }
        }

        var localDeployments = new List<DeploymentInformation>();
        foreach (var functionName in _registry.GetRegisteredFunctionNames())
        {
            var config = _registry.GetConfiguration(functionName);
            if (config == null) continue;

            var connections = _registry.GetConnections(functionName);
            if (connections.Count == 0) continue;

            localDeployments.Add(CreateDeployment(functionName, config,
                connections.AsValueEnumerable().Select(static connection => connection.ConnectionId)));
        }
        return localDeployments;
    }

    internal static DeploymentInformation CreateDeployment(string functionName, WebSocketFunctionConfiguration config,
        IEnumerable<string> connectionIds) => CreateDeployment(functionName, config, connectionIds.AsValueEnumerable());

    private static DeploymentInformation CreateDeployment<TEnumerator>(string functionName, WebSocketFunctionConfiguration config,
        ValueEnumerable<TEnumerator, string> connectionIds) where TEnumerator : struct, IValueEnumerator<string>, allows ref struct
    {
            var pods = connectionIds
                .Select(id => new PodInformation(
                    Name: $"ws-{id[..8]}",
                    Started: true,
                    Ready: true,
                    Ip: id,
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

            var subscribeEvents = config.SubscribeEvents.AsValueEnumerable()
                .Select(e =>
                {
                    FunctionVisibility evtVisibility = visibility; // hérite de DefaultVisibility par défaut
                    if (!string.IsNullOrWhiteSpace(e.Visibility))
                    {
                        evtVisibility = e.Visibility.Equals("Private", StringComparison.OrdinalIgnoreCase)
                            ? FunctionVisibility.Private
                            : FunctionVisibility.Public;
                    }
                    return new SubscribeEvent(e.Name, evtVisibility);
                })
                .ToList();

            var pathsVisibility = config.PathsStartWithVisibility.AsValueEnumerable()
                .Select(static p => new PathVisibility(
                    p.Path,
                    p.Visibility.Equals("Private", StringComparison.OrdinalIgnoreCase)
                        ? FunctionVisibility.Private
                        : FunctionVisibility.Public))
                .ToList();

            var info = new DeploymentInformation(
                Deployment: functionName,
                Namespace: "websocket-virtual",
                Pods: pods,
                Configuration: config.ParsedConfiguration,
                Replicas: pods.Count,
                ReplicasAtStart: pods.Count,
                ReplicasMin: 0,
                TimeoutSecondBeforeSetReplicasMin: 0,
                NumberParallelRequest: config.NumberParallelRequest,
                ReplicasStartAsSoonAsOneFunctionRetrieveARequest: config.ReplicasStartAsSoonAsOneFunctionRetrieveARequest,
                PodType: PodType.WebSocket,
                DependsOn: config.DependsOn,
                SubscribeEvents: subscribeEvents,
                Visibility: visibility,
                PathsStartWithVisibility: pathsVisibility,
                ResourceVersion: "ws",
                EndpointReady: true,
                Trust: trust,
                NumberParallelRequestPerPod: config.NumberParallelRequestPerPod,
                SubscribeStreams: config.ParsedStreams,
                StreamMaxAgeSeconds: config.StreamMaxAgeSeconds,
                StreamMaxLength: config.StreamMaxLength
            );

            return info;
    }
}
