using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SlimFaas.Kubernetes;
using System.Globalization;
using SlimFaas.Streams;

namespace SlimFaas.WebSocket;

/// <summary>
/// Données d'un stream synchrone en cours côté serveur.
/// </summary>
public sealed class PendingSyncStream : IDisposable
{
    /// <summary>Résultat du SyncResponseStart (status + headers).</summary>
    public TaskCompletionSource<SyncResponseStartPayload> ResponseStartTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Channel alimenté par les SyncResponseChunk du client.</summary>
    public Channel<byte[]> ResponseChunks { get; } = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true });

    /// <summary>Signalé quand SyncResponseEnd est reçu.</summary>
    public TaskCompletionSource ResponseEndTcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>CancellationTokenSource lié à l'annulation du stream.</summary>
    public CancellationTokenSource Cts { get; } = new();
    internal CancellationToken Token { get; }
    public PendingSyncStream() => Token = Cts.Token;
    public void Dispose() => Cts.Dispose();
    internal async Task CancelAsync()
    {
        try { await Cts.CancelAsync(); }
        catch (ObjectDisposedException) when (Token.IsCancellationRequested) { }
    }
}

/// <summary>
/// Représente une connexion WebSocket d'un client (job ou fonction virtuelle).
/// Conceptuellement équivalent à un "pod" avec une IP.
/// </summary>
public sealed class WebSocketClientConnection : IDisposable
{
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
    public string FunctionName { get; set; } = string.Empty;
    public WebSocketFunctionConfiguration Configuration { get; set; } = new();
    public System.Net.WebSockets.WebSocket Socket { get; set; } = null!;

    /// <summary>Tâche en attente de callback (elementId -> TaskCompletionSource&lt;int&gt;).</summary>
    public ConcurrentDictionary<string, TaskCompletionSource<int>> PendingCallbacks { get; } = new();
    public ConcurrentDictionary<string, byte> StreamRequests { get; } = new();
    internal IStreamService? Streams { get; set; }
    internal HttpContext? RequestContext { get; set; }

    /// <summary>Streams synchrones en cours (correlationId -> PendingSyncStream).</summary>
    public ConcurrentDictionary<string, PendingSyncStream> PendingSyncStreams { get; } = new();

    /// <summary>Nombre de requêtes en cours de traitement par ce client.</summary>
    public int ActiveRequests => PendingCallbacks.Count + PendingSyncStreams.Count + StreamRequests.Count;

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public bool IsAlive => Socket.State == WebSocketState.Open;
    internal bool Ready { get; set; } = true;

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _reservedRequests;

    internal bool TryReserveRequest(int maximum)
    {
        while (true)
        {
            int current = Volatile.Read(ref _reservedRequests);
            if (Math.Max(current, ActiveRequests) >= maximum) return false;
            if (Interlocked.CompareExchange(ref _reservedRequests, current + 1, current) == current) return true;
        }
    }

    internal void ReleaseRequest() => Interlocked.Decrement(ref _reservedRequests);

    public void Dispose()
    {
        _sendLock.Dispose();
        GC.SuppressFinalize(this);
    }

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

    /// <summary>Envoie une frame binaire brute au client WebSocket.</summary>
    public async Task SendBinaryAsync(byte[] frame, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            await Socket.SendAsync(
                new ArraySegment<byte>(frame),
                System.Net.WebSockets.WebSocketMessageType.Binary,
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
    private readonly ConcurrentDictionary<string, ConcurrentBag<WebSocketClientConnection>> _connections = new(StringComparer.OrdinalIgnoreCase);

    // Verrou par functionName pour valider la configuration à la connexion
    private readonly ConcurrentDictionary<string, WebSocketFunctionConfiguration> _registeredConfigurations = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<WebSocketConnectionRegistry> _logger;
    private readonly object _registrationLock = new();
    private int _connectionCounter;

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
        try
        {
            ParseConfiguration(name, connection.Configuration);
        }
        catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
        {
            return (false, $"Invalid function configuration: {exception.Message}");
        }

        return RegisterValidated(connection, replicasService);
    }

    internal static void ParseConfiguration(string name, WebSocketFunctionConfiguration configuration)
    {
            var metadata = SlimFaas.Kubernetes.FunctionMetadataParser.Parse(
                new Dictionary<string, string>
                {
                    [FunctionAnnotationNames.Configuration] = configuration.Configuration,
                    [FunctionAnnotationNames.DefaultVisibility] = configuration.DefaultVisibility,
                    [FunctionAnnotationNames.SubscribeStream] = string.Join(',', configuration.SubscribeStreams.Select(item =>
                        (item.Visibility ?? configuration.DefaultVisibility) + ":" + item.Name)),
                    [FunctionAnnotationNames.StreamMaxAge] = configuration.StreamMaxAgeSeconds.ToString(CultureInfo.InvariantCulture),
                    [FunctionAnnotationNames.StreamMaxLength] = configuration.StreamMaxLength.ToString(CultureInfo.InvariantCulture)
                }, name);
            configuration.ParsedConfiguration = metadata.Configuration;
            configuration.ParsedStreams = metadata.SubscribeStreams ?? [];
    }

    private (bool Success, string? Error) RegisterValidated(WebSocketClientConnection connection, IReplicasService replicasService)
    {
        lock (_registrationLock)
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

        _logger.LogWebSocketClientRegisteredConnectionIdFunctionName(connection.ConnectionId, name);

        return (true, null);
        }
    }

    /// <summary>
    /// Supprime une connexion du registre.
    /// </summary>
    public void Unregister(WebSocketClientConnection connection)
    {
        lock (_registrationLock)
        {
        if (!_connections.TryGetValue(connection.FunctionName, out var bag))
        {
            return;
        }

        if (!bag.Any(candidate => candidate.ConnectionId == connection.ConnectionId)) return;

        // ConcurrentBag ne supporte pas de suppression directe ; on reconstruit
        var remaining = bag.Where(c => c.ConnectionId != connection.ConnectionId).ToList();
        var newBag = new ConcurrentBag<WebSocketClientConnection>(remaining);
        _connections.TryUpdate(connection.FunctionName, newBag, bag);

        if (newBag.IsEmpty)
        {
            _registeredConfigurations.TryRemove(connection.FunctionName, out _);
        }

        Interlocked.Decrement(ref _connectionCounter);

        _logger.LogWebSocketClientUnregisteredConnectionIdFunctionName(connection.ConnectionId, connection.FunctionName);
        }
    }

    /// <summary>
    /// Retourne toutes les connexions actives pour un nom de fonction donné.
    /// </summary>
    public IReadOnlyList<WebSocketClientConnection> GetConnections(string functionName)
    {
        if (_connections.TryGetValue(functionName, out var bag))
        {
            return bag.Where(c => c.IsAlive && c.Ready).ToList();
        }
        return Array.Empty<WebSocketClientConnection>();
    }

    /// <summary>
    /// Retourne tous les noms de fonctions enregistrées avec des clients WebSocket actifs.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredFunctionNames()
    {
        return _connections
            .Where(kvp => kvp.Value.Any(c => c.IsAlive && c.Ready))
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

    // Round-robin index par functionName
    private readonly ConcurrentDictionary<string, int> _roundRobinIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sélectionne le prochain client WebSocket en round-robin pour une fonction donnée,
    /// en respectant la limite <c>NumberParallelRequestPerPod</c>.
    /// Un client dont <see cref="WebSocketClientConnection.ActiveRequests"/> a atteint
    /// <paramref name="maxPerPod"/> est sauté. Si tous les clients sont saturés, retourne null.
    /// </summary>
    public WebSocketClientConnection? SelectNextRoundRobin(string functionName, int maxPerPod = int.MaxValue,
        bool reserve = false, string? excludedElementId = null)
    {
        var connections = GetConnections(functionName);
        if (connections.Count == 0)
        {
            return null;
        }

        int startIndex = _roundRobinIndex.AddOrUpdate(functionName, 0, (_, prev) => prev + 1);

        // Parcourir tous les clients à partir de l'index round-robin,
        // sélectionner le premier qui n'a pas atteint la limite per-pod.
        for (int i = 0; i < connections.Count; i++)
        {
            int index = (int)((uint)unchecked(startIndex + i) % (uint)connections.Count);
            var candidate = connections[index];
            if (candidate.ActiveRequests < maxPerPod &&
                (excludedElementId is null || !candidate.PendingCallbacks.ContainsKey(excludedElementId)) &&
                (!reserve || candidate.TryReserveRequest(maxPerPod)))
            {
                return candidate;
            }
        }

        // Tous les clients sont saturés
        return null;
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
               && a.StreamMaxAgeSeconds == b.StreamMaxAgeSeconds
               && a.StreamMaxLength == b.StreamMaxLength
               && a.SubscribeStreams.Count == b.SubscribeStreams.Count
               && a.SubscribeStreams.OrderBy(x => x.Name).Zip(b.SubscribeStreams.OrderBy(x => x.Name))
                   .All(pair => pair.First.Name == pair.Second.Name && pair.First.Visibility == pair.Second.Visibility)
               && a.SubscribeEvents.Count == b.SubscribeEvents.Count
               && a.SubscribeEvents.OrderBy(x => x.Name).Zip(b.SubscribeEvents.OrderBy(x => x.Name))
                   .All(pair => pair.First.Name == pair.Second.Name && pair.First.Visibility == pair.Second.Visibility)
               && a.DependsOn.OrderBy(x => x).SequenceEqual(b.DependsOn.OrderBy(x => x))
               && a.PathsStartWithVisibility.Count == b.PathsStartWithVisibility.Count
               && a.PathsStartWithVisibility.OrderBy(x => x.Path).Zip(b.PathsStartWithVisibility.OrderBy(x => x.Path))
                   .All(pair => pair.First.Path == pair.Second.Path && pair.First.Visibility == pair.Second.Visibility);
    }
}
