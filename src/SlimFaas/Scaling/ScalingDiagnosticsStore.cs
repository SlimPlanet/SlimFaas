using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Scaling;

// Diagnostic retention is independent of the histories used to make scaling decisions.
public sealed class ScalingDiagnosticsStore(IOptions<SlimFaasOptions> options, TimeProvider? clock = null)
{
    internal const int MaxEvents = 300;
    internal const long MaxBytes = 8 * 1024 * 1024;
    internal const int MaxSnapshotBytes = 256 * 1024;
    private readonly object _sync = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Function, long Id, long TimestampMs, string Json)> _events = new();
    private string _session = Guid.NewGuid().ToString("N");
    private bool _leader;
    private long _sequence, _bytes;
    private sealed class Entry
    {
        public string Json = "";
        public string Configuration = "null";
        public string Signature = "";
        public long Updated;
        public bool Truncated;
        public LinkedList<LinkedListNode<(string Function, long Id, long TimestampMs, string Json)>> Events = new();
    }

    internal long RetainedBytes { get { lock (_sync) return _bytes; } }
    internal string Session { get { lock (_sync) return _session; } }

    public void SetLeadership(bool leader)
    {
        lock (_sync)
        {
            if (_leader == leader) return;
            _leader = leader;
            _session = Guid.NewGuid().ToString("N");
            _entries.Clear(); _events.Clear(); _bytes = 0; _sequence = 0;
        }
    }

    internal void Record(ScalingDecision decision, ScaleConfig? configuration, string? expectedSession = null)
    {
        if (!options.Value.EnableFront) return;
        string json = JsonSerializer.Serialize(decision, ScalingJsonContext.Default.ScalingDecision);
        string config;
        bool invalidConfiguration = false;
        try { config = JsonSerializer.Serialize(configuration, ScaleConfigSerializerContext.Default.ScaleConfig); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        {
            // Diagnostics must never prevent a decision for an existing malformed configuration.
            config = "null"; invalidConfiguration = true;
        }
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        lock (_sync)
        {
            if (!_leader || expectedSession is not null && expectedSession != _session) return;
            Prune(now);
            if (!_entries.TryGetValue(decision.Function, out var entry))
            {
                _entries.Add(decision.Function, entry = new());
                _bytes += Size(entry.Json) + Size(entry.Configuration) + Size(entry.Signature) + Size(decision.Function);
            }
            _bytes -= Size(entry.Json) + Size(entry.Configuration);
            if (Size(json) + Size(config) > MaxSnapshotBytes)
            {
                entry.Json = ""; entry.Configuration = "null"; entry.Truncated = true;
            }
            else { entry.Json = json; entry.Configuration = config; }
            entry.Truncated |= invalidConfiguration;
            entry.Updated = now;
            _bytes += Size(entry.Json) + Size(entry.Configuration);
            var reasons = decision.Reasons.Select(r => r.Code).ToArray();
            var signals = decision.Triggers.Select(t => $"{t.Index}:{t.State}")
                .Concat(decision.Sources.Select(s => $"{s.Name}:{s.State}")).ToArray();
            var item = new ScalingEvent(0, now, decision.CurrentReplicas, decision.Target, decision.RawTarget,
                decision.Action, decision.Application, reasons, signals);
            string signature = JsonSerializer.Serialize(item with { TimestampMs = 0 }, ScalingJsonContext.Default.ScalingEvent);
            if (entry.Signature != signature)
            {
                _bytes -= Size(entry.Signature);
                entry.Signature = Size(signature) <= 8192 ? signature : "";
                _bytes += Size(entry.Signature);
                var ev = item with { Id = ++_sequence };
                string eventJson = JsonSerializer.Serialize(ev, ScalingJsonContext.Default.ScalingEvent);
                if (Size(eventJson) <= 8192)
                {
                    var node = _events.AddLast((decision.Function, ev.Id, now, eventJson));
                    entry.Events.AddLast(node);
                    _bytes += Size(eventJson);
                    while (entry.Events.Count > MaxEvents) RemoveEvent(entry.Events.First!.Value);
                }
                else entry.Truncated = true;
            }
            while (_bytes > MaxBytes && _events.First is not null) RemoveEvent(_events.First);
            while (_bytes > MaxBytes && _entries.Count > 0)
            {
                var oldest = _entries.MinBy(e => e.Value.Updated);
                RemoveEntry(oldest.Key);
            }
        }
    }

    public ScalingState GetState(string function, int ready, int requested, int interval)
    {
        lock (_sync)
        {
            long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            Prune(now);
            _entries.TryGetValue(function, out var entry);
            var decision = string.IsNullOrEmpty(entry?.Json) ? null :
                JsonSerializer.Deserialize(entry.Json, ScalingJsonContext.Default.ScalingDecision);
            var config = entry is null ? null : JsonSerializer.Deserialize(entry.Configuration, ScaleConfigSerializerContext.Default.ScaleConfig);
            var history = new List<ScalingEvent>();
            long historyBytes = 0;
            foreach (var ev in entry?.Events.Reverse() ?? [])
            {
                historyBytes += Size(ev.Value.Json);
                if (historyBytes > MaxSnapshotBytes) break;
                history.Add(JsonSerializer.Deserialize(ev.Value.Json, ScalingJsonContext.Default.ScalingEvent)!);
            }
            history.Reverse();
            var events = history.ToArray();
            return new(_session, !_leader ? "LeaderUnavailable" : decision is null ? "WaitingForDecision" : "Live",
                now, function, decision, config, ready, requested, events, (entry?.Truncated ?? false) || (entry?.Events.Count ?? 0) > events.Length, interval);
        }
    }

    internal void Retain(IReadOnlySet<string> functions)
    {
        lock (_sync)
            foreach (string name in _entries.Keys.Where(k => !functions.Contains(k)).ToArray()) RemoveEntry(name);
    }

    private static long Size(string value) => 2L * value.Length + 256;

    private void Prune(long now)
    {
        long cutoff = now - 15 * 60 * 1000;
        while (_events.First is { } ev && ev.Value.TimestampMs < cutoff) RemoveEvent(ev);
        foreach (string key in _entries.Where(e => e.Value.Updated < cutoff).Select(e => e.Key).ToArray()) RemoveEntry(key);
    }

    private void RemoveEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        while (entry.Events.First is { } node) RemoveEvent(node.Value);
        _bytes -= Size(entry.Json) + Size(entry.Configuration) + Size(entry.Signature) + Size(key);
        _entries.Remove(key);
    }

    private void RemoveEvent(LinkedListNode<(string Function, long Id, long TimestampMs, string Json)> node)
    {
        if (_entries.TryGetValue(node.Value.Function, out var entry))
        {
            entry.Events.Remove(node);
            entry.Truncated = true;
        }
        _bytes -= Size(node.Value.Json);
        _events.Remove(node);
    }
}
