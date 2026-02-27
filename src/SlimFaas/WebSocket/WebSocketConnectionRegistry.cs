using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SlimFaas.Kubernetes;

namespace SlimFaas.WebSocket;

/// <summary>
/// Représente une connexion WebSocket d'un client (job ou fonction virtuelle).
/// Conceptuellement équivalent à un "pod" avec une IP.
/// </summary>
public class WebSocketClientConnection
{
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public string FunctionName { get; set; } = string.Empty;
    public WebSocketFunctionConfiguration Configuration { get; set; } = new();
    public System.Net.WebSockets.WebSocket Socket { get; set; } = null!;

    /// <summary>Tâche en attente de callback (elementId -> TaskCompletionSource<int>).</summary>
    public ConcurrentDictionary<string, TaskCompletionSource<int>> PendingCallbacks { get; } = new();

    /// <summary>Nombre de requêtes en cours de traitement par ce client.</summary>
    public int ActiveRequests => PendingCallbacks.Count;

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public bool IsAlive => Socket.State == WebSocketState.Open;

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public async Task SendAsync(WebSocketEnvelope envelope, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(envelope, AppJsonContext.Default.WebSocketEnvelope);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(
                new ArraySegment<byte>(bytes),
                System.Net.WebSockets.WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
/// <summary>
/// Registre thread-safe des connexions WebSocket actives.
/// Une connexion = un "replica virtuel" d'une fonction ou d'un job.
/// </summary>
public class WebSocketConnectionRegistry
{
    // functionName -> liste des connexions actives
    private readonly ConcurrentDictionary<string, ConcurrentBag<WebSocketClientConnection>> _connections = new();

    // Verrou par functionName pour valider la configuration à la connexion
    private readonly ConcurrentDictionary<string, WebSocketFunctionConfiguration> _registeredConfigurations = new();

    private readonly ILogger<WebSocketConnectionRegistry> _logger;
    private int _connectionCounter = 0;

    public WebSocketConnectionRegistry(ILogger<WebSocketConnectionRegistry> logger)
    {
        _logger = logger;
    }

    public int TotalConnections => _connectionCounter;

    /// <summary>
    /// Tente d'enregistrer une nouvelle connexion.
    /// Retourne une erreur si la configuration diffère de celle déjà enregistrée pour ce nom.
    /// </summary>
    public (bool Success, string? Error) TryRegister(
        WebSocketClientConnection connection,
        IReplicasService replicasService)
    {
        string name = connection.FunctionName;

        // Vérification : ce nom est-il déjà une fonction Kubernetes ?
        bool existsAsK8sFunction = replicasService.Deployments.Functions
            .Any(f => string.Equals(f.Deployment, name, StringComparison.OrdinalIgnoreCase));

        if (existsAsK8sFunction)
        {
            return (false, $"Function '{name}' is already declared as a Kubernetes deployment. " +
                           "WebSocket clients cannot use the same name as an existing Kubernetes function.");
        }

        // Vérification de la cohérence de configuration
        if (_registeredConfigurations.TryGetValue(name, out var existingConfig))
        {
            if (!ConfigurationsAreEqual(existingConfig, connection.Configuration))
            {
                return (false, $"Function '{name}' already has registered clients with a different configuration. " +
                               "All WebSocket clients with the same function name must share the same configuration.");
            }
        }
        else
        {
            _registeredConfigurations.TryAdd(name, connection.Configuration);
        }

        var bag = _connections.GetOrAdd(name, _ => new ConcurrentBag<WebSocketClientConnection>());
        bag.Add(connection);
        Interlocked.Increment(ref _connectionCounter);

        _logger.LogInformation(
            "WebSocket client registered: connectionId={ConnectionId}, functionName={FunctionName}",
            connection.ConnectionId, name);

        return (true, null);
    }

    /// <summary>
    /// Supprime une connexion du registre.
    /// </summary>
    public void Unregister(WebSocketClientConnection connection)
    {
        if (!_connections.TryGetValue(connection.FunctionName, out var bag))
        {
            return;
        }

        // ConcurrentBag ne supporte pas de suppression directe ; on reconstruit
        var remaining = bag.Where(c => c.ConnectionId != connection.ConnectionId).ToList();
        var newBag = new ConcurrentBag<WebSocketClientConnection>(remaining);
        _connections.TryUpdate(connection.FunctionName, newBag, bag);

        if (newBag.IsEmpty)
        {
            _registeredConfigurations.TryRemove(connection.FunctionName, out _);
        }

        Interlocked.Decrement(ref _connectionCounter);

        _logger.LogInformation(
            "WebSocket client unregistered: connectionId={ConnectionId}, functionName={FunctionName}",
            connection.ConnectionId, connection.FunctionName);
    }

    /// <summary>
    /// Retourne toutes les connexions actives pour un nom de fonction donné.
    /// </summary>
    public IReadOnlyList<WebSocketClientConnection> GetConnections(string functionName)
    {
        if (_connections.TryGetValue(functionName, out var bag))
        {
            return bag.Where(c => c.IsAlive).ToList();
        }
        return Array.Empty<WebSocketClientConnection>();
    }

    /// <summary>
    /// Retourne tous les noms de fonctions enregistrées avec des clients WebSocket actifs.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredFunctionNames()
    {
        return _connections
            .Where(kvp => kvp.Value.Any(c => c.IsAlive))
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <summary>
    /// Retourne la configuration enregistrée pour un nom de fonction.
    /// </summary>
    public WebSocketFunctionConfiguration? GetConfiguration(string functionName)
    {
        _registeredConfigurations.TryGetValue(functionName, out var config);
        return config;
    }

    /// <summary>
    /// Sélectionne le client le moins chargé (round-robin pondéré) pour une fonction donnée.
    /// </summary>
    public WebSocketClientConnection? SelectLeastBusy(string functionName)
    {
        var connections = GetConnections(functionName);
        if (connections.Count == 0)
        {
            return null;
        }

        return connections.OrderBy(c => c.ActiveRequests).First();
    }

    private static bool ConfigurationsAreEqual(
        WebSocketFunctionConfiguration a,
        WebSocketFunctionConfiguration b)
    {
        return a.DefaultVisibility == b.DefaultVisibility
               && a.DefaultTrust == b.DefaultTrust
               && a.NumberParallelRequest == b.NumberParallelRequest
               && a.NumberParallelRequestPerPod == b.NumberParallelRequestPerPod
               && a.ReplicasStartAsSoonAsOneFunctionRetrieveARequest == b.ReplicasStartAsSoonAsOneFunctionRetrieveARequest
               && a.Configuration == b.Configuration
               && a.SubscribeEvents.OrderBy(x => x).SequenceEqual(b.SubscribeEvents.OrderBy(x => x))
               && a.DependsOn.OrderBy(x => x).SequenceEqual(b.DependsOn.OrderBy(x => x))
               && a.PathsStartWithVisibility.Count == b.PathsStartWithVisibility.Count
               && a.PathsStartWithVisibility.All(kvp =>
                   b.PathsStartWithVisibility.TryGetValue(kvp.Key, out var val) && val == kvp.Value);
    }
}

