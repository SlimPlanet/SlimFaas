using System.Collections.Immutable;
using MemoryPack;
using DotNext.IO;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class WebSocketDirectoryTests
{
    private static readonly long Now = DateTime.UtcNow.Ticks;
    private static SlimDataState Create() => new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

    private static WebSocketDirectoryMutation Mutation(WebSocketDirectoryAction action, string node,
        string incarnation, string? connectionId = null, long? now = null, string? fingerprint = null) => new()
    {
        Action = action, Node = node, Incarnation = incarnation, ConnectionId = connectionId ?? "",
        Function = "billing", Configuration = "{}", Fingerprint = fingerprint ?? new string('A', 64),
        NowTicks = now ?? Now, RelaySecret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
    };

    private static string Start(SlimDataState state, string node)
    {
        string incarnation = Guid.NewGuid().ToString("N");
        Assert.Equal(200, WebSocketDirectoryStore.Apply(Mutation(WebSocketDirectoryAction.StartNode, node, incarnation), state).Status);
        return incarnation;
    }

    private static string Register(SlimDataState state, string node, string incarnation, bool activate = true)
    {
        string id = Guid.NewGuid().ToString("N");
        Assert.Equal(200, WebSocketDirectoryStore.Apply(Mutation(WebSocketDirectoryAction.Reserve, node, incarnation, id), state).Status);
        if (activate)
            Assert.Equal(200, WebSocketDirectoryStore.Apply(Mutation(WebSocketDirectoryAction.Activate, node, incarnation, id), state).Status);
        return id;
    }

    [Fact]
    public void Provisional_registration_blocks_conflicts_without_advertising_a_route()
    {
        var state = Create();
        string first = Start(state, "node-a");
        string second = Start(state, "node-b");
        string id = Register(state, "node-a", first, activate: false);
        Assert.Empty(WebSocketDirectoryStore.Connections(state.CapturePayload(), Now));
        var rejected = WebSocketDirectoryStore.Apply(Mutation(WebSocketDirectoryAction.Reserve, "node-b", second,
            Guid.NewGuid().ToString("N"), fingerprint: new string('B', 64)), state);
        Assert.Equal(409, rejected.Status);
        Assert.Single(WebSocketDirectoryStore.Connections(state.CapturePayload(), Now, includeProvisional: true));
        Assert.Equal(200, WebSocketDirectoryStore.Apply(Mutation(WebSocketDirectoryAction.Activate, "node-a", first, id), state).Status);
        Assert.Equal(id, Assert.Single(WebSocketDirectoryStore.Connections(state.CapturePayload(), Now)).Id);
    }

    [Fact]
    public void Compatible_clients_on_different_nodes_are_unique_replicas()
    {
        var state = Create();
        string first = Start(state, "node-a");
        string second = Start(state, "node-b");
        string a = Register(state, "node-a", first);
        string b = Register(state, "node-b", second);
        var connections = WebSocketDirectoryStore.Connections(state.CapturePayload(), Now);
        Assert.Equal(2, connections.Count);
        Assert.Equal(2, connections.Select(connection => connection.Id).Distinct().Count());
        Assert.Contains(connections, connection => connection.Id == a && connection.Node == "node-a");
        Assert.Contains(connections, connection => connection.Id == b && connection.Node == "node-b");
    }

    [Fact]
    public void Disconnect_removes_only_its_connection_and_is_idempotent()
    {
        var state = Create();
        string incarnation = Start(state, "node");
        string first = Register(state, "node", incarnation);
        string second = Register(state, "node", incarnation);
        var remove = Mutation(WebSocketDirectoryAction.Remove, "node", incarnation, first);
        Assert.Equal(200, WebSocketDirectoryStore.Apply(remove, state).Status);
        Assert.Equal(200, WebSocketDirectoryStore.Apply(remove, state).Status);
        Assert.Equal(second, Assert.Single(WebSocketDirectoryStore.Connections(state.CapturePayload(), Now)).Id);
    }

    [Fact]
    public void Restart_fences_old_renewals_activations_and_removals()
    {
        var state = Create();
        string old = Start(state, "node");
        string oldId = Register(state, "node", old);
        string current = Start(state, "node");
        string currentId = Register(state, "node", current);
        Assert.DoesNotContain(WebSocketDirectoryStore.Connections(state.CapturePayload(), Now), connection => connection.Id == oldId);
        foreach (var action in new[] { WebSocketDirectoryAction.RenewNode, WebSocketDirectoryAction.Activate, WebSocketDirectoryAction.Remove })
            Assert.Equal(409, WebSocketDirectoryStore.Apply(Mutation(action, "node", old, currentId), state).Status);
        Assert.Equal(currentId, Assert.Single(WebSocketDirectoryStore.Connections(state.CapturePayload(), Now)).Id);
        Assert.Equal(current, WebSocketDirectoryStore.Owner(state.CapturePayload(), "node")!.Incarnation);
    }

    [Fact]
    public void Expired_owner_is_not_routable_and_cleanup_removes_its_descriptors()
    {
        var state = Create();
        string incarnation = Start(state, "node");
        Register(state, "node", incarnation);
        long expired = Now + WebSocketDirectoryStore.LeaseSeconds * TimeSpan.TicksPerSecond;
        Assert.Empty(WebSocketDirectoryStore.Connections(state.CapturePayload(), expired));
        Assert.Equal(409, WebSocketDirectoryStore.Apply(
            Mutation(WebSocketDirectoryAction.RenewNode, "node", incarnation, now: expired), state).Status);
        string another = Guid.NewGuid().ToString("N");
        WebSocketDirectoryStore.Apply(Mutation(WebSocketDirectoryAction.StartNode, "another", another, now: expired), state);
        Assert.Null(WebSocketDirectoryStore.Owner(state.CapturePayload(), "node"));
        Assert.Empty(WebSocketDirectoryStore.Connections(state.CapturePayload(), expired, includeProvisional: true));
    }

    [Fact]
    public void Renewals_extend_the_lease_without_changing_connection_identity()
    {
        var state = Create();
        string incarnation = Start(state, "node");
        string id = Register(state, "node", incarnation);
        long renewed = Now + 10 * TimeSpan.TicksPerSecond;
        Assert.Equal(200, WebSocketDirectoryStore.Apply(
            Mutation(WebSocketDirectoryAction.RenewNode, "node", incarnation, now: renewed), state).Status);
        Assert.Equal(id, Assert.Single(WebSocketDirectoryStore.Connections(state.CapturePayload(),
            Now + 31 * TimeSpan.TicksPerSecond)).Id);
        Assert.Equal(renewed + WebSocketDirectoryStore.LeaseSeconds * TimeSpan.TicksPerSecond,
            WebSocketDirectoryStore.Owner(state.CapturePayload(), "node")!.ExpiresAtTicks);
    }

    [Fact]
    public void Mutation_roundtrip_preserves_owner_and_configuration()
    {
        var mutation = Mutation(WebSocketDirectoryAction.Reserve, "node", Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        var restored = MemoryPackSerializer.Deserialize<WebSocketDirectoryMutation>(WebSocketDirectoryStore.SerializeMutation(mutation))!;
        Assert.Equal(mutation.Node, restored.Node);
        Assert.Equal(mutation.Incarnation, restored.Incarnation);
        Assert.Equal(mutation.ConnectionId, restored.ConnectionId);
        Assert.Equal(mutation.Fingerprint, restored.Fingerprint);
        Assert.Equal(mutation.Configuration, restored.Configuration);
    }

    [Fact]
    public async Task Snapshot_roundtrip_preserves_owner_lease_and_connection_configuration()
    {
        var state = Create();
        string incarnation = Start(state, "node");
        string id = Register(state, "node", incarnation);
        using var stream = new MemoryStream();
        await SlimDataSnapshotSerializer.WriteAsync(IAsyncBinaryWriter.Create(stream, new byte[256]),
            new SlimDataStateSnapshot(state.Hashsets, state.KeyValues, state.Queues), CancellationToken.None);
        stream.Position = 0;
        var snapshot = await SlimDataSnapshotSerializer.ReadAsync(IAsyncBinaryReader.Create(stream, new byte[256]), CancellationToken.None);
        var restored = new SlimDataState(snapshot.Hashsets, snapshot.KeyValues, snapshot.Queues);
        Assert.Equal(id, Assert.Single(WebSocketDirectoryStore.Connections(restored.CapturePayload(), Now)).Id);
        Assert.Equal(WebSocketDirectoryStore.Owner(state.CapturePayload(), "node")!.RelaySecret,
            WebSocketDirectoryStore.Owner(restored.CapturePayload(), "node")!.RelaySecret);
        Assert.All(state.Hashsets.Keys, key => Assert.StartsWith("\0websocket:", key, StringComparison.Ordinal));
    }
}
