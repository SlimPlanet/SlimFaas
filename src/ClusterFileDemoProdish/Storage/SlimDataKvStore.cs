using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace ClusterFileDemoProdish.Storage;

/// <summary>
/// Demo implementation that looks like a SlimData KV, including an exposed SlimDataState.
/// TTL is enforced on read, but entries remain visible for the cleanup worker until removed.
/// </summary>
public sealed class SlimDataKvStore : IKvStore
{
    private sealed record Entry(byte[] Value, long? ExpiresUtcMs);

    private readonly ConcurrentDictionary<string, Entry> _kv = new();

    public SlimDataState SlimDataState { get; private set; } = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<object>>.Empty
    );

    public Task<byte[]?> GetAsync(string key)
    {
        if (!_kv.TryGetValue(key, out var entry))
            return Task.FromResult<byte[]?>(null);

        if (entry.ExpiresUtcMs is long exp && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= exp)
        {
            _kv.TryRemove(key, out _);
            RebuildSlimDataState();
            return Task.FromResult<byte[]?>(null);
        }

        return Task.FromResult<byte[]?>(entry.Value);
    }

    public Task SetAsync(string key, byte[] value, long? timeToLiveMilliseconds = null)
    {
        long? expiresUtcMs = null;
        if (timeToLiveMilliseconds is long ttl && ttl > 0)
            expiresUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ttl;

        _kv[key] = new Entry(value, expiresUtcMs);
        RebuildSlimDataState();
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key)
    {
        _kv.TryRemove(key, out _);
        RebuildSlimDataState();
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, (byte[] Value, long? ExpiresUtcMs)> Snapshot()
        => _kv.ToDictionary(kvp => kvp.Key, kvp => (kvp.Value.Value, kvp.Value.ExpiresUtcMs));

    private void RebuildSlimDataState()
    {
        // For the demo we rebuild a KV-only view.
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var keyValues = _kv
            .Where(kvp => kvp.Value.ExpiresUtcMs is null || kvp.Value.ExpiresUtcMs > nowMs)
            .ToImmutableDictionary(
                kvp => kvp.Key,
                kvp => (ReadOnlyMemory<byte>)kvp.Value.Value
            );

        SlimDataState = SlimDataState with { KeyValues = keyValues };
    }
}
