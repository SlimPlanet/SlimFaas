using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Workers;

/// <summary>Shares recent peer activity only while a local Traffic stream is subscribed.</summary>
public class NetworkActivitySyncWorker(
    IReplicasService replicasService,
    NetworkActivityTracker tracker,
    IHttpClientFactory httpClientFactory,
    IOptions<SlimFaasOptions> slimFaasOptions,
    INamespaceProvider namespaceProvider,
    ILogger<NetworkActivitySyncWorker> logger) : BackgroundService
{
    private sealed record Cursor(long TimestampMs, string? Instance, long ReadStartedAt);
    private readonly ConcurrentDictionary<string, Cursor> _peers = new(StringComparer.Ordinal);
    private long _session;
    private HttpClient? _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!slimFaasOptions.Value.EnableFront) return;
        var options = slimFaasOptions.Value.StatusStream;
        await Task.Delay(options.PeerSyncInitialDelayMilliseconds, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ScrapeAllPeersAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogDebug(ex, "Could not synchronize peer activity"); }
            await Task.Delay(options.PeerSyncIntervalMilliseconds, stoppingToken);
        }
    }

    internal async Task ScrapeAllPeersAsync(CancellationToken ct)
    {
        long session = tracker.LiveSessionStartedAt;
        if (session != _session) { _peers.Clear(); _session = session; }
        if (session == 0 || !tracker.HasSubscribers) return;
        var pods = replicasService.Deployments?.SlimFaas?.Pods;
        if (pods == null || pods.Count <= 1) { _peers.Clear(); return; }

        _client ??= httpClientFactory.CreateClient("ActivitySync");
        var peers = pods.Where(p => p.Ready == true && !string.IsNullOrEmpty(p.Ip)
                && !string.Equals(p.Name, tracker.NodeId, StringComparison.OrdinalIgnoreCase))
            .Select(p => {
                var uri = ActivityEndpoint(p, slimFaasOptions.Value, namespaceProvider.CurrentNamespace);
                return (Key: p.Name, Uri: uri);
            }).Where(p => p.Uri != null)
            .Select(p => (Key: string.IsNullOrEmpty(p.Key) ? p.Uri!.Authority : p.Key,
                Url: $"{p.Uri!.Scheme}://{p.Uri.Authority}"))
            .DistinctBy(p => p.Key).ToArray();
        var active = peers.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _peers.Keys.Where(k => !active.Contains(k))) _peers.TryRemove(key, out _);

        await Parallel.ForEachAsync(peers, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (peer, token) => {
                if (tracker.LiveSessionStartedAt != session || !tracker.HasSubscribers) return;
                try { await ScrapePeerAsync(peer.Key, peer.Url, session, token); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                { logger.LogDebug(ex, "Could not read activity from {Peer}", peer.Key); }
            });
    }

    internal static Uri? ActivityEndpoint(PodInformation pod, SlimFaasOptions options, string ns)
    {
        // Native nodes advertise [Raft, HTTP]; Kubernetes pod port order can vary.
        // Use the same exclusion as SlimFaasPorts rather than the first declared port.
        var applicationPod = pod with { EndpointUrl = null };
        if (options.BaseFunctionPodUrl.Contains("{pod_port}", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(SlimDataEndpoint.Get(pod, options.BaseSlimDataUrl, ns), UriKind.Absolute, out var raft)) return null;
            var ports = pod.Ports?.Where(port => port != raft.Port).Distinct().ToList();
            if (ports is not { Count: > 0 }) return null;
            applicationPod = applicationPod with { Ports = ports };
        }
        // Explicit ports and indexed port placeholders retain their configured meaning.
        return Uri.TryCreate(SlimDataEndpoint.Get(applicationPod, options.BaseFunctionPodUrl, ns), UriKind.Absolute, out var endpoint)
            ? endpoint : null;
    }

    private async Task ScrapePeerAsync(string key, string url, long session, CancellationToken ct)
    {
        _peers.TryGetValue(key, out var cursor);
        long readStarted = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        // A restarted peer can have a different clock. One bounded retry reads its
        // recent window using its own clock rather than the former process cursor.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            long windowStart = cursor?.ReadStartedAt ?? session;
            string query = cursor == null || attempt > 0
                ? $"windowMs={(long)Math.Ceiling(Stopwatch.GetElapsedTime(windowStart).TotalMilliseconds) + 1}"
                : $"since={Math.Max(0, cursor.TimestampMs - 1)}";
            using var response = await _client!.GetAsync($"{url}/internal/activity-events?{query}", timeout.Token);
            if (!response.IsSuccessStatusCode) return;
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var events = JsonSerializer.Deserialize(json, StatusStreamSerializerContext.Default.ListNetworkActivityEvent) ?? [];
            string? instance = response.Headers.TryGetValues("X-Activity-Instance", out var instances) ? instances.FirstOrDefault() : null;
            if (attempt == 0 && cursor?.Instance != null && instance != null && cursor.Instance != instance) continue;
            bool hasWatermark = response.Headers.TryGetValues("X-Activity-Watermark", out var values)
                && long.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            long watermark = hasWatermark ? long.Parse(values!.First(), CultureInfo.InvariantCulture)
                : events.Count > 0 ? events.Max(e => e.TimestampMs) : cursor?.TimestampMs ?? 0;
            if (tracker.LiveSessionStartedAt != session || !tracker.HasSubscribers) return;
            // Older peers ignore windowMs: prime their cursor without replaying history.
            if (cursor != null || hasWatermark) tracker.IngestRemote(events);
            _peers[key] = new Cursor(watermark, instance, readStarted);
            return;
        }
    }
}
