using System.Collections.Immutable;
using MemoryPack;
using SlimData.Commands;

namespace SlimData;

public enum WebSocketDirectoryAction { StartNode, RenewNode, Reserve, Activate, Remove, StopNode }

[MemoryPackable]
public sealed partial class WebSocketDirectoryOwner
{
    public string Node { get; set; } = "";
    public string Incarnation { get; set; } = "";
    public string RelaySecret { get; set; } = "";
    public long ExpiresAtTicks { get; set; }
}

[MemoryPackable]
public sealed partial class WebSocketDirectoryConnection
{
    public string Id { get; set; } = "";
    public string Function { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Node { get; set; } = "";
    public string Incarnation { get; set; } = "";
    public bool Ready { get; set; }
}

[MemoryPackable]
public sealed partial class WebSocketDirectoryMutation
{
    public WebSocketDirectoryAction Action { get; set; }
    public string Node { get; set; } = "";
    public string Incarnation { get; set; } = "";
    public string RelaySecret { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string Function { get; set; } = "";
    public string Configuration { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public long NowTicks { get; set; }
}

[MemoryPackable]
public sealed partial class WebSocketDirectoryResult
{
    public int Status { get; set; } = 200;
    public string? Error { get; set; }
}

public static class WebSocketDirectoryStore
{
    public const string Key = "\0websocket:directory";
    public const int LeaseSeconds = 30;
    private const string OwnersKey = "\0websocket:owners";
    private const string ConnectionsKey = "\0websocket:connections";

    private static ImmutableDictionary<string, ReadOnlyMemory<byte>> Hash(SlimDataPayload payload, string key) =>
        payload.Hashsets.GetValueOrDefault(key) ?? ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;

    public static byte[] SerializeMutation(WebSocketDirectoryMutation mutation) => MemoryPackPayload.Serialize(mutation);
    public static (object Owners, object Connections) Revision(SlimDataPayload payload) =>
        (Hash(payload, OwnersKey), Hash(payload, ConnectionsKey));

    public static WebSocketDirectoryOwner? Owner(SlimDataPayload payload, string node) =>
        Hash(payload, OwnersKey).TryGetValue(node, out var bytes)
            ? MemoryPackSerializer.Deserialize<WebSocketDirectoryOwner>(bytes.Span) : null;

    public static IReadOnlyList<WebSocketDirectoryConnection> Connections(SlimDataPayload payload, long nowTicks,
        bool includeProvisional = false)
    {
        var result = new List<WebSocketDirectoryConnection>();
        foreach (var bytes in Hash(payload, ConnectionsKey).Values)
        {
            var connection = MemoryPackSerializer.Deserialize<WebSocketDirectoryConnection>(bytes.Span)
                ?? throw new InvalidDataException("Invalid WebSocket directory connection.");
            var owner = Owner(payload, connection.Node);
            if (owner is not null && owner.Incarnation == connection.Incarnation && owner.ExpiresAtTicks > nowTicks &&
                (connection.Ready || includeProvisional))
                result.Add(connection);
        }
        return result;
    }

    public static void Validate(WebSocketDirectoryMutation mutation)
    {
        if (!Enum.IsDefined(mutation.Action) || string.IsNullOrWhiteSpace(mutation.Node) ||
            !Guid.TryParseExact(mutation.Incarnation, "N", out _) || mutation.NowTicks <= 0 ||
            mutation.NowTicks > DateTime.MaxValue.Ticks - LeaseSeconds * TimeSpan.TicksPerSecond)
            throw new InvalidDataException("Invalid WebSocket directory owner.");
        if (mutation.Action == WebSocketDirectoryAction.StartNode &&
            (!Convert.TryFromBase64String(mutation.RelaySecret, new byte[32], out var written) || written != 32))
            throw new InvalidDataException("Invalid WebSocket relay credential.");
        if (mutation.Action is WebSocketDirectoryAction.Reserve or WebSocketDirectoryAction.Activate or WebSocketDirectoryAction.Remove &&
            !Guid.TryParseExact(mutation.ConnectionId, "N", out _))
            throw new InvalidDataException("Invalid WebSocket connection identity.");
        if (mutation.Action == WebSocketDirectoryAction.Reserve &&
            (string.IsNullOrWhiteSpace(mutation.Function) || mutation.Function.Length > 256 ||
             mutation.Function.Any(char.IsControl) || mutation.Configuration.Length > 1024 * 1024 ||
             mutation.Fingerprint.Length != 64))
            throw new InvalidDataException("Invalid WebSocket function registration.");
    }

    public static WebSocketDirectoryResult Apply(WebSocketDirectoryMutation mutation, SlimDataState state)
    {
        Validate(mutation);
        var payload = state.CapturePayload();
        var owner = Owner(payload, mutation.Node);
        var connections = Hash(payload, ConnectionsKey);
        if (mutation.Action == WebSocketDirectoryAction.StartNode)
        {
            if (owner is not null && owner.Incarnation != mutation.Incarnation)
                connections = WithoutOwner(connections, mutation.Node, owner.Incarnation);
            owner = new()
            {
                Node = mutation.Node, Incarnation = mutation.Incarnation, RelaySecret = mutation.RelaySecret,
                ExpiresAtTicks = mutation.NowTicks + LeaseSeconds * TimeSpan.TicksPerSecond
            };
        }
        else if (owner is null || owner.Incarnation != mutation.Incarnation || owner.ExpiresAtTicks <= mutation.NowTicks)
            return Error(409, "WebSocket owner lease is unavailable or superseded.");

        switch (mutation.Action)
        {
            case WebSocketDirectoryAction.RenewNode:
                owner.ExpiresAtTicks = Math.Max(owner.ExpiresAtTicks, mutation.NowTicks + LeaseSeconds * TimeSpan.TicksPerSecond);
                break;
            case WebSocketDirectoryAction.Reserve:
                if (Connections(payload, mutation.NowTicks, includeProvisional: true).Any(connection =>
                        connection.Function.Equals(mutation.Function, StringComparison.OrdinalIgnoreCase) &&
                        connection.Fingerprint != mutation.Fingerprint))
                    return Error(409, "All WebSocket clients with the same function name must share the same configuration.");
                if (connections.TryGetValue(mutation.ConnectionId, out var reservedBytes))
                {
                    var reserved = MemoryPackSerializer.Deserialize<WebSocketDirectoryConnection>(reservedBytes.Span)!;
                    if (reserved.Node != mutation.Node || reserved.Incarnation != mutation.Incarnation ||
                        reserved.Function != mutation.Function || reserved.Fingerprint != mutation.Fingerprint)
                        return Error(409, "WebSocket connection identity is already reserved.");
                    break;
                }
                connections = connections.Add(mutation.ConnectionId, MemoryPackPayload.Serialize(new WebSocketDirectoryConnection
                {
                    Id = mutation.ConnectionId, Function = mutation.Function, Configuration = mutation.Configuration,
                    Fingerprint = mutation.Fingerprint, Node = mutation.Node, Incarnation = mutation.Incarnation
                }));
                break;
            case WebSocketDirectoryAction.Activate:
            case WebSocketDirectoryAction.Remove:
                if (connections.TryGetValue(mutation.ConnectionId, out var bytes))
                {
                    var connection = MemoryPackSerializer.Deserialize<WebSocketDirectoryConnection>(bytes.Span)!;
                    if (connection.Node != mutation.Node || connection.Incarnation != mutation.Incarnation)
                        return Error(409, "WebSocket connection belongs to another owner.");
                    if (mutation.Action == WebSocketDirectoryAction.Remove)
                        connections = connections.Remove(mutation.ConnectionId);
                    else
                    {
                        connection.Ready = true;
                        connections = connections.SetItem(connection.Id, MemoryPackPayload.Serialize(connection));
                    }
                }
                else if (mutation.Action == WebSocketDirectoryAction.Activate)
                    return Error(409, "WebSocket connection reservation has expired.");
                break;
            case WebSocketDirectoryAction.StopNode:
                connections = WithoutOwner(connections, mutation.Node, mutation.Incarnation);
                owner.ExpiresAtTicks = mutation.NowTicks;
                break;
        }

        var owners = Hash(payload, OwnersKey).SetItem(mutation.Node, MemoryPackPayload.Serialize(owner));
        foreach (var (node, bytes) in owners)
        {
            var expired = MemoryPackSerializer.Deserialize<WebSocketDirectoryOwner>(bytes.Span)!;
            if (expired.ExpiresAtTicks > mutation.NowTicks) continue;
            owners = owners.Remove(node);
            connections = WithoutOwner(connections, node, expired.Incarnation);
        }
        state.Hashsets = state.Hashsets.SetItem(OwnersKey, owners).SetItem(ConnectionsKey, connections);
        return new();
    }

    private static ImmutableDictionary<string, ReadOnlyMemory<byte>> WithoutOwner(
        ImmutableDictionary<string, ReadOnlyMemory<byte>> connections, string node, string incarnation) =>
        connections.RemoveRange(connections.Where(pair =>
        {
            var connection = MemoryPackSerializer.Deserialize<WebSocketDirectoryConnection>(pair.Value.Span)!;
            return connection.Node == node && connection.Incarnation == incarnation;
        }).Select(pair => pair.Key));

    private static WebSocketDirectoryResult Error(int status, string message) => new() { Status = status, Error = message };
}
