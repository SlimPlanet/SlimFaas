using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

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
    string? TargetPod = null,   // target pod name or IP (e.g. the downstream pod receiving the request)
    string? CorrelationId = null); // shared id used to pair related start/end events while keeping Id unique

/// <summary>
/// Represents the full stream payload sent via SSE.
/// </summary>
public record SlimFaasNodeInfo(string Name, string Status);

public record StatusStreamPayload(
    IReadOnlyList<FunctionStatusDetailed> Functions,
    IList<QueueInfo> Queues,
    IList<JobConfigurationStatus> Jobs,
    IList<NetworkActivityEvent> RecentActivity,
    int SlimFaasReplicas = 1,
    IList<SlimFaasNodeInfo>? SlimFaasNodes = null,
    bool FrontEnabled = true,
    string? FrontMessage = null);

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
[JsonSerializable(typeof(JobConfigurationStatus))]
[JsonSerializable(typeof(List<JobConfigurationStatus>))]
[JsonSerializable(typeof(RunningJobStatus))]
[JsonSerializable(typeof(List<RunningJobStatus>))]
[JsonSerializable(typeof(ScheduledJobInfo))]
[JsonSerializable(typeof(List<ScheduledJobInfo>))]
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
    public static class EventTypes
    {
        public const string RequestIn = "request_in";
        public const string Enqueue = "enqueue";
        public const string Dequeue = "dequeue";
        public const string RequestOut = "request_out";
        public const string Response = "response";
        public const string EventPublish = "event_publish";
        public const string RequestWaiting = "request_waiting";
        public const string RequestStarted = "request_started";
        public const string RequestEnd = "request_end";
    }

    public static class Actors
    {
        public const string External = "external";
        public const string SlimFaas = "slimfaas";
    }

    private readonly ConcurrentQueue<NetworkActivityEvent> _recentEvents = new();
    private readonly ConcurrentDictionary<Channel<NetworkActivityEvent>, byte> _subscribers = new();
    private readonly ConcurrentDictionary<string, byte> _knownIds = new();
    private int _counter;
    private int _recentEventsCount;
    private int _knownIdsCount;
    private int _knownIdsTrimInProgress;
    private int _subscriberCount;
    private readonly object _liveRateLimitLock = new();
    private long _liveRateLimitWindowSecond;
    private int _liveRateLimitWindowCount;
    private readonly bool _enabled;
    private readonly StatusStreamOptions _options;

    public NetworkActivityTracker()
    {
        _enabled = true;
        _options = new StatusStreamOptions();
    }

    public NetworkActivityTracker(IOptions<SlimFaasOptions> slimFaasOptions)
    {
        _enabled = slimFaasOptions.Value.EnableFront;
        _options = slimFaasOptions.Value.StatusStream;
    }

    /// <summary>Hostname of the current node (set once at startup).</summary>
    public string NodeId { get; } = Environment.GetEnvironmentVariable("HOSTNAME")
                                    ?? Environment.MachineName
                                    ?? Guid.NewGuid().ToString("N")[..8];

    public bool HasSubscribers => _enabled && !_subscribers.IsEmpty;

    /// <summary>Record a local activity event and broadcast to all SSE subscribers.</summary>
    public string Record(string type, string source, string target, string? queueName = null, string? sourcePod = null, string? targetPod = null, string? correlationId = null)
    {
        if (!_enabled) return string.Empty;

        var evt = new NetworkActivityEvent(
            Id: $"{NodeId}-{Interlocked.Increment(ref _counter)}",
            Type: type,
            Source: source,
            Target: target,
            QueueName: queueName,
            TimestampMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            NodeId: NodeId,
            SourcePod: sourcePod,
            TargetPod: targetPod,
            CorrelationId: correlationId);

        Enqueue(evt);
        return evt.Id;
    }

    /// <summary>
    /// Ingest events received from a remote peer node.
    /// Deduplicates by Id so the same event is never stored twice.
    /// </summary>
    public int IngestRemote(IEnumerable<NetworkActivityEvent> remoteEvents)
    {
        if (!_enabled) return 0;

        int count = 0;
        foreach (var evt in remoteEvents)
        {
            if (_knownIds.TryAdd(evt.Id, 0))
            {
                Interlocked.Increment(ref _knownIdsCount);
                Enqueue(evt, knownIdAlreadyAdded: true);
                count++;
            }
        }
        return count;
    }

    /// <summary>Get a snapshot of all recent events (local + remote).</summary>
    public IList<NetworkActivityEvent> GetRecent()
    {
        if (!_enabled) return Array.Empty<NetworkActivityEvent>();
        return _recentEvents.ToArray();
    }

    /// <summary>Get only local events recorded since a given timestamp.</summary>
    public List<NetworkActivityEvent> GetLocalSince(long sinceTimestampMs)
    {
        if (!_enabled) return new List<NetworkActivityEvent>();

        return _recentEvents
            .Where(e => e.NodeId == NodeId && e.TimestampMs > sinceTimestampMs)
            .ToList();
    }

    /// <summary>Subscribe to live events. Returns a ChannelReader.</summary>
    public (ChannelReader<NetworkActivityEvent> Reader, Channel<NetworkActivityEvent> Channel) Subscribe()
    {
        if (TrySubscribe(out var reader, out var channel))
        {
            return (reader, channel);
        }

        return (reader, channel);
    }

    /// <summary>Try to subscribe to live events, respecting the configured SSE client limit.</summary>
    public bool TrySubscribe(out ChannelReader<NetworkActivityEvent> reader, out Channel<NetworkActivityEvent> channel)
    {
        channel = Channel.CreateBounded<NetworkActivityEvent>(
            // Large buffer to absorb short traffic spikes and avoid dropping paired
            // request_out/request_end events that drive in-flight counters on the UI.
            new BoundedChannelOptions(Math.Max(1, _options.SubscriberChannelCapacity))
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
        reader = channel.Reader;

        if (!_enabled)
        {
            return true;
        }

        int maxSseClients = _options.MaxSseClients;
        if (maxSseClients > 0)
        {
            while (true)
            {
                int current = Volatile.Read(ref _subscriberCount);
                if (current >= maxSseClients)
                {
                    channel.Writer.TryComplete();
                    return false;
                }

                if (Interlocked.CompareExchange(ref _subscriberCount, current + 1, current) == current)
                {
                    break;
                }
            }
        }

        if (_subscribers.TryAdd(channel, 0))
        {
            if (maxSseClients <= 0)
            {
                Interlocked.Increment(ref _subscriberCount);
            }

            return true;
        }

        Interlocked.Decrement(ref _subscriberCount);
        channel.Writer.TryComplete();
        return false;
    }

    /// <summary>Unsubscribe from live events.</summary>
    public void Unsubscribe(Channel<NetworkActivityEvent> channel)
    {
        if (_subscribers.TryRemove(channel, out _))
        {
            Interlocked.Decrement(ref _subscriberCount);
        }

        channel.Writer.TryComplete();
    }

    private void Enqueue(NetworkActivityEvent evt, bool knownIdAlreadyAdded = false)
    {
        if (!knownIdAlreadyAdded && _knownIds.TryAdd(evt.Id, 0))
        {
            Interlocked.Increment(ref _knownIdsCount);
        }

        _recentEvents.Enqueue(evt);
        Interlocked.Increment(ref _recentEventsCount);

        int recentActivityLimit = Math.Max(1, _options.RecentActivityLimit);
        while (Volatile.Read(ref _recentEventsCount) > recentActivityLimit && _recentEvents.TryDequeue(out _))
        {
            Interlocked.Decrement(ref _recentEventsCount);
        }

        TrimKnownIdsIfNeeded();

        if (_subscribers.IsEmpty || !ShouldBroadcastLiveEvent())
        {
            return;
        }

        // Broadcast to SSE subscribers. Enumerating the dictionary directly avoids
        // allocating/enumerating a Keys collection on every activity event.
        foreach (var subscriber in _subscribers)
        {
            var channel = subscriber.Key;
            if (channel.Reader.Completion.IsCompleted)
            {
                if (_subscribers.TryRemove(channel, out _))
                {
                    Interlocked.Decrement(ref _subscriberCount);
                }

                continue;
            }

            channel.Writer.TryWrite(evt);
        }
    }

    private void TrimKnownIdsIfNeeded()
    {
        int knownIdsLimit = Math.Max(1, _options.KnownIdsLimit);
        if (Volatile.Read(ref _knownIdsCount) <= knownIdsLimit)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _knownIdsTrimInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            int toRemove = Volatile.Read(ref _knownIdsCount) - knownIdsLimit / 2;
            if (toRemove <= 0)
            {
                return;
            }

            foreach (var knownId in _knownIds)
            {
                if (toRemove <= 0)
                {
                    break;
                }

                if (_knownIds.TryRemove(knownId.Key, out _))
                {
                    Interlocked.Decrement(ref _knownIdsCount);
                    toRemove--;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _knownIdsTrimInProgress, 0);
        }
    }

    private bool ShouldBroadcastLiveEvent()
    {
        double samplingRatio = _options.LiveEventSamplingRatio;
        if (samplingRatio <= 0)
        {
            return false;
        }

        if (samplingRatio < 1 && Random.Shared.NextDouble() >= samplingRatio)
        {
            return false;
        }

        int maxLiveEventsPerSecond = _options.MaxLiveEventsPerSecond;
        if (maxLiveEventsPerSecond <= 0)
        {
            return true;
        }

        long currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_liveRateLimitLock)
        {
            if (_liveRateLimitWindowSecond != currentSecond)
            {
                _liveRateLimitWindowSecond = currentSecond;
                _liveRateLimitWindowCount = 0;
            }

            if (_liveRateLimitWindowCount >= maxLiveEventsPerSecond)
            {
                return false;
            }

            _liveRateLimitWindowCount++;
            return true;
        }
    }
}






