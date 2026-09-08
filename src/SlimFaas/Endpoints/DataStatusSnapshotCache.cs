using DotNext;
using MemoryPack;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Commands;
using SlimData.Expiration;
using SlimFaas.Options;

namespace SlimFaas.Endpoints;

/// <summary>A metadata-only projection, shared per node and refreshed lazily while a browser requests pages.</summary>
public sealed class DataStatusSnapshotCache(ISupplier<SlimDataPayload> state, IOptions<SlimFaasOptions> options,
    TimeProvider? timeProvider = null)
{
    private const string SetPrefix = "data:set:";
    private readonly object _gate = new();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private Snapshot? _snapshot;
    private long _refreshAtMs;

    private sealed record Snapshot(DataStatusEntry[] Sets, DataStatusEntry[] Files, DataStatusSummary Summary);

    public DataStatusPage GetPage(string kind, string prefix, string after, int limit)
    {
        var now = _clock.GetUtcNow();
        Snapshot snapshot;
        lock (_gate)
        {
            if (_snapshot is null || now.ToUnixTimeMilliseconds() >= _refreshAtMs)
            {
                _snapshot = BuildSnapshot(now);
                _refreshAtMs = now.ToUnixTimeMilliseconds() + options.Value.StatusStream.StateIntervalMilliseconds;
            }
            snapshot = _snapshot;
        }

        var entries = kind == "files" ? snapshot.Files : snapshot.Sets;
        int start = LowerBound(entries, prefix);
        int end = start;
        if (prefix.Length == 0) end = entries.Length;
        else while (end < entries.Length && entries[end].Id.StartsWith(prefix, StringComparison.Ordinal)) end++;
        int pageStart = Math.Max(start, LowerBound(entries, after));
        if (pageStart < end && entries[pageStart].Id == after) pageStart++;
        pageStart = Math.Min(pageStart, end);
        int count = Math.Min(limit, end - pageStart);
        var page = entries.AsSpan(pageStart, count).ToArray();
        string? next = pageStart + count < end && count > 0 ? page[^1].Id : null;
        return new DataStatusPage(kind, now.ToUnixTimeMilliseconds(), page, next, end - start, snapshot.Summary,
            options.Value.StatusStream.StateIntervalMilliseconds);
    }

    private Snapshot BuildSnapshot(DateTimeOffset now)
    {
        var values = state.Invoke().KeyValues;
        var sets = new List<DataStatusEntry>();
        var files = new List<DataStatusEntry>();
        long nowMs = now.ToUnixTimeMilliseconds();
        foreach (var pair in values)
        {
            string key = pair.Key;
            bool isSet = key.StartsWith(SetPrefix, StringComparison.Ordinal) &&
                !key.EndsWith(SlimDataInterpreter.TimeToLivePostfix, StringComparison.Ordinal);
            bool isFile = key.StartsWith(DataFileKeys.MetaPrefix, StringComparison.Ordinal) &&
                key.EndsWith(DataFileKeys.MetaSuffix, StringComparison.Ordinal);
            if (!isSet && !isFile) continue;
            string id = isSet ? key[SetPrefix.Length..] : key[DataFileKeys.MetaPrefix.Length..^DataFileKeys.MetaSuffix.Length];
            if (!IdValidator.IsSafeId(id) || (isFile && DataFileKeys.IsInternalElementId(id))) continue;

            long? expiry = null;
            if (values.TryGetValue(key + SlimDataInterpreter.TimeToLivePostfix, out var ttl) &&
                SlimDataExpirationCleaner.TryReadInt64(ttl, out long ticks) && ticks > 0)
            {
                if (ticks <= now.UtcTicks) continue;
                if (ticks <= DateTime.MaxValue.Ticks) expiry = new DateTimeOffset(ticks, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }

            long? length = null;
            if (isFile)
            {
                try
                {
                    var metadata = MemoryPackSerializer.Deserialize<DataSetMetadata>(pair.Value.Span);
                    if (metadata?.Length >= 0) length = metadata.Length;
                }
                catch (MemoryPackSerializationException)
                {
                    // Keep the key visible without exposing raw metadata or fetching the document.
                }
            }

            (isSet ? sets : files).Add(new DataStatusEntry(id, expiry, length));
        }

        sets.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        files.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        var summary = new DataStatusSummary(sets.Count, files.Count, files.Sum(f => f.SizeBytes ?? 0),
            files.Count(f => f.SizeBytes is null),
            sets.Concat(files).Count(e => e.ExpiresAtMs is long expiry && expiry <= nowMs + 60_000));
        return new Snapshot(sets.ToArray(), files.ToArray(), summary);
    }

    private static int LowerBound(DataStatusEntry[] entries, string key)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (string.CompareOrdinal(entries[mid].Id, key) < 0) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
