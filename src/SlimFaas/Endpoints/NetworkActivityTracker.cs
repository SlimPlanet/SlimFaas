using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using SlimFaas.Kubernetes;

namespace SlimFaas.Endpoints;

/// <summary>
/// Represents a single network message flowing through SlimFaas.
/// </summary>
public record NetworkActivityEvent(
    string Id,
    string Type,          // "request_in", "enqueue", "dequeue", "request_out", "response", "event_publish", "request_waiting", "request_started", "request_end"
    string Source,        // e.g. "external", function name, "slimfaas"
    string Target,        // e.g. "slimfaas", function name, "external"
    string? QueueName,    // the queue name if relevant
    long TimestampMs,
    string NodeId,        // hostname of the SlimFaas node that recorded the event
    string? SourcePod = null,   // source pod name or IP (e.g. the caller pod)
    string? TargetPod = null);  // target pod name or IP (e.g. the downstream pod receiving the request)

/// <summary>
/// Represents the full stream payload sent via SSE.
/// </summary>
public record SlimFaasNodeInfo(string Name, string Status);

public record StatusStreamPayload(
    IReadOnlyList<FunctionStatusDetailed> Functions,
    IList<QueueInfo> Queues,
    IList<NetworkActivityEvent> RecentActivity,
    int SlimFaasReplicas = 1,
    IList<SlimFaasNodeInfo>? SlimFaasNodes = null);

public record QueueInfo(string Name, long Length);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(StatusStreamPayload))]
[JsonSerializable(typeof(NetworkActivityEvent))]
[JsonSerializable(typeof(List<NetworkActivityEvent>))]
[JsonSerializable(typeof(QueueInfo))]
[JsonSerializable(typeof(List<QueueInfo>))]
[JsonSerializable(typeof(SlimFaasNodeInfo))]
[JsonSerializable(typeof(List<SlimFaasNodeInfo>))]
[JsonSerializable(typeof(FunctionStatusDetailed))]
[JsonSerializable(typeof(List<FunctionStatusDetailed>))]
[JsonSerializable(typeof(PodStatus))]
[JsonSerializable(typeof(ResourcesConfiguration))]
[JsonSerializable(typeof(ScheduleConfig))]
[JsonSerializable(typeof(ScaleConfig))]
[JsonSerializable(typeof(SubscribeEvent))]
[JsonSerializable(typeof(PathVisibility))]
public partial class StatusStreamSerializerContext : JsonSerializerContext
{
}

/// <summary>
/// Thread-safe tracker that records network activity events (messages flowing through SlimFaas)
/// and dispatches them to connected SSE subscribers via a Channel.
/// In a multi-node cluster, each node has its own tracker; a background worker periodically
/// fetches events from peer nodes and ingests them here for a global view.
/// </summary>
public sealed class NetworkActivityTracker
{
    private readonly ConcurrentQueue<NetworkActivityEvent> _recentEvents = new();
    private readonly ConcurrentBag<Channel<NetworkActivityEvent>> _subscribers = new();
    private readonly ConcurrentDictionary<string, byte> _knownIds = new();
    private int _counter;

    /// <summary>Hostname of the current node (set once at startup).</summary>
    public string NodeId { get; } = Environment.GetEnvironmentVariable("HOSTNAME")
                                    ?? Environment.MachineName
                                    ?? Guid.NewGuid().ToString("N")[..8];

    private const int MaxRecentEvents = 200;
    private const int MaxKnownIds = 1000;

    /// <summary>Record a local activity event and broadcast to all SSE subscribers.</summary>
    public string Record(string type, string source, string target, string? queueName = null, string? sourcePod = null, string? targetPod = null)
    {
        var evt = new NetworkActivityEvent(
            Id: $"{NodeId}-{Interlocked.Increment(ref _counter)}",
            Type: type,
            Source: source,
            Target: target,
            QueueName: queueName,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            NodeId: NodeId,
            SourcePod: sourcePod,
            TargetPod: targetPod);

        Enqueue(evt);
        return evt.Id;
    }

    /// <summary>
    /// Ingest events received from a remote peer node.
    /// Deduplicates by Id so the same event is never stored twice.
    /// </summary>
    public int IngestRemote(IEnumerable<NetworkActivityEvent> remoteEvents)
    {
        int count = 0;
        foreach (var evt in remoteEvents)
        {
            if (_knownIds.TryAdd(evt.Id, 0))
            {
                Enqueue(evt);
                count++;
            }
        }
        return count;
    }

    /// <summary>Get a snapshot of all recent events (local + remote).</summary>
    public IList<NetworkActivityEvent> GetRecent()
    {
        return _recentEvents.ToArray();
    }

    /// <summary>Get only local events recorded since a given timestamp.</summary>
    public List<NetworkActivityEvent> GetLocalSince(long sinceTimestampMs)
    {
        return _recentEvents
            .Where(e => e.NodeId == NodeId && e.TimestampMs > sinceTimestampMs)
            .ToList();
    }

    /// <summary>Subscribe to live events. Returns a ChannelReader.</summary>
    public (ChannelReader<NetworkActivityEvent> Reader, Channel<NetworkActivityEvent> Channel) Subscribe()
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<NetworkActivityEvent>(
            new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        _subscribers.Add(channel);
        return (channel.Reader, channel);
    }

    /// <summary>Unsubscribe from live events.</summary>
    public void Unsubscribe(Channel<NetworkActivityEvent> channel)
    {
        channel.Writer.TryComplete();
    }

    private void Enqueue(NetworkActivityEvent evt)
    {
        _knownIds.TryAdd(evt.Id, 0);
        _recentEvents.Enqueue(evt);

        while (_recentEvents.Count > MaxRecentEvents)
            _recentEvents.TryDequeue(out _);

        // Trim the dedup set
        if (_knownIds.Count > MaxKnownIds)
        {
            // Remove oldest half (arbitrary, cheap enough)
            int toRemove = _knownIds.Count - MaxKnownIds / 2;
            foreach (var key in _knownIds.Keys.Take(toRemove))
                _knownIds.TryRemove(key, out _);
        }

        // Broadcast to SSE subscribers
        foreach (var channel in _subscribers)
        {
            channel.Writer.TryWrite(evt);
        }
    }
}






