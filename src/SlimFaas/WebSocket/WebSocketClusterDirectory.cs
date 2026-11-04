using System.Security.Cryptography;
using System.Text.Json;
using DotNext;
using SlimData;
using SlimData.Commands;
using SlimFaas.Endpoints;
using SlimFaas.Streams;

namespace SlimFaas.WebSocket;

public interface IWebSocketClusterDirectory
{
    string Node { get; }
    string Incarnation { get; }
    IReadOnlyList<WebSocketDirectoryConnection> Connections { get; }
    WebSocketDirectoryOwner? Owner(string node);
    Task RefreshAsync(CancellationToken cancellationToken);
    Task<WebSocketDirectoryResult> ReserveAsync(WebSocketClientConnection connection, CancellationToken cancellationToken);
    Task<WebSocketDirectoryResult> ActivateAsync(WebSocketClientConnection connection, CancellationToken cancellationToken);
    Task RemoveAsync(WebSocketClientConnection connection, CancellationToken cancellationToken);
}

public sealed class WebSocketClusterDirectory(IDatabaseService database, ISupplier<SlimDataPayload> state,
    WebSocketConnectionRegistry registry, NetworkActivityTracker activity,
    ILogger<WebSocketClusterDirectory> logger) : BackgroundService, IWebSocketClusterDirectory
{
    private readonly SemaphoreSlim _initializing = new(1, 1);
    private readonly string _relaySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private volatile bool _started;
    private readonly object _cacheLock = new();
    private object? _ownersRevision;
    private object? _connectionsRevision;
    private long _cacheExpires;
    private IReadOnlyList<WebSocketDirectoryConnection> _connections = [];
    public string Node => activity.NodeId;
    public string Incarnation { get; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<WebSocketDirectoryConnection> Connections
    {
        get
        {
            var payload = state.Invoke();
            var revision = WebSocketDirectoryStore.Revision(payload);
            long now = DateTime.UtcNow.Ticks;
            lock (_cacheLock)
            {
                if (ReferenceEquals(_ownersRevision, revision.Owners) &&
                    ReferenceEquals(_connectionsRevision, revision.Connections) && now < _cacheExpires)
                    return _connections;
                _ownersRevision = revision.Owners;
                _connectionsRevision = revision.Connections;
                _connections = WebSocketDirectoryStore.Connections(payload, now);
                _cacheExpires = _connections.Count == 0 ? now + TimeSpan.TicksPerSecond :
                    _connections.Min(connection => WebSocketDirectoryStore.Owner(payload, connection.Node)!.ExpiresAtTicks);
                return _connections;
            }
        }
    }
    public WebSocketDirectoryOwner? Owner(string node) => WebSocketDirectoryStore.Owner(state.Invoke(), node);

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        _ = await database.GetAsync(WebSocketDirectoryStore.Key).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await _initializing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            var result = await MutateAsync(new()
            {
                Action = WebSocketDirectoryAction.StartNode, RelaySecret = _relaySecret
            }, cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            _started = true;
        }
        finally { _initializing.Release(); }
    }

    public async Task<WebSocketDirectoryResult> ReserveAsync(WebSocketClientConnection connection, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await MutateAsync(new()
        {
            Action = WebSocketDirectoryAction.Reserve, ConnectionId = connection.ConnectionId,
            Function = connection.FunctionName, Fingerprint = StreamConfiguration.Fingerprint(connection.Configuration),
            Configuration = JsonSerializer.Serialize(connection.Configuration, AppJsonContext.Default.WebSocketFunctionConfiguration)
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WebSocketDirectoryResult> ActivateAsync(WebSocketClientConnection connection, CancellationToken cancellationToken) =>
        await MutateAsync(new() { Action = WebSocketDirectoryAction.Activate, ConnectionId = connection.ConnectionId },
            cancellationToken).ConfigureAwait(false);

    public async Task RemoveAsync(WebSocketClientConnection connection, CancellationToken cancellationToken)
    {
        if (!_started) return;
        var result = await MutateAsync(new()
        {
            Action = WebSocketDirectoryAction.Remove, ConnectionId = connection.ConnectionId
        }, cancellationToken).ConfigureAwait(false);
        if (result.Status >= 400) logger.LogDirectoryMutationRejected(result.Status, result.Error);
    }

    private async Task<WebSocketDirectoryResult> MutateAsync(WebSocketDirectoryMutation mutation, CancellationToken cancellationToken)
    {
        mutation.Node = Node;
        mutation.Incarnation = Incarnation;
        mutation.NowTicks = DateTime.UtcNow.Ticks;
        var result = await database.ExecuteWebSocketDirectoryAsync(mutation).WaitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static void RequireSuccess(WebSocketDirectoryResult result)
    {
        if (result.Status >= 400)
            throw new InvalidOperationException(result.Error ?? "WebSocket directory mutation failed.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureStartedAsync(stoppingToken).ConfigureAwait(false);
                var renewed = await MutateAsync(new() { Action = WebSocketDirectoryAction.RenewNode }, stoppingToken)
                    .ConfigureAwait(false);
                if (renewed.Status >= 400)
                {
                    var current = Owner(Node);
                    if (current is not null && current.Incarnation != Incarnation)
                        throw new InvalidOperationException("This WebSocket node incarnation was superseded.");
                    _started = false;
                    await EnsureStartedAsync(stoppingToken).ConfigureAwait(false);
                }
                var advertised = Connections.Select(connection => connection.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var connection in registry.GetRegisteredFunctionNames().SelectMany(registry.GetConnections))
                {
                    if (advertised.Contains(connection.ConnectionId)) continue;
                    RequireSuccess(await ReserveAsync(connection, stoppingToken).ConfigureAwait(false));
                    if (!connection.Ready || !connection.IsAlive)
                    {
                        await RemoveAsync(connection, stoppingToken).ConfigureAwait(false);
                        continue;
                    }
                    RequireSuccess(await ActivateAsync(connection, stoppingToken).ConfigureAwait(false));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogDirectoryMaintenanceFailed(exception);
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        if (!_started) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            RequireSuccess(await MutateAsync(new() { Action = WebSocketDirectoryAction.StopNode }, cleanup.Token)
                .ConfigureAwait(false));
            _started = false;
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException or InvalidOperationException)
        {
            logger.LogDirectoryMaintenanceFailed(exception);
        }
    }

    public override void Dispose()
    {
        _initializing.Dispose();
        base.Dispose();
    }
}
