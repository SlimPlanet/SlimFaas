using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimFaas.Endpoints;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Workers;

/// <summary>
/// Background worker that periodically scrapes activity events from all peer SlimFaas nodes
/// via <c>GET /internal/activity-events?since={ms}</c> and ingests them into the local
/// <see cref="NetworkActivityTracker"/> so that the SSE stream presents a cluster-wide view.
/// </summary>
public class NetworkActivitySyncWorker(
    IReplicasService replicasService,
    NetworkActivityTracker tracker,
    IHttpClientFactory httpClientFactory,
    IOptions<SlimFaasOptions> slimFaasOptions,
    INamespaceProvider namespaceProvider,
    ILogger<NetworkActivitySyncWorker> logger)
    : BackgroundService
{
    // Scrape peers every 2 seconds (same cadence as the SSE state push)
    private const int DelayMs = 2000;

    // Track the last timestamp we fetched per peer to ask only for newer events
    private readonly Dictionary<string, long> _peerLastTimestamp = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!slimFaasOptions.Value.EnableFront)
        {
            logger.LogInformation("NetworkActivitySyncWorker disabled because SlimFaas:EnableFront=false");
            return;
        }

        // Small initial delay to let the cluster form
        await Task.Delay(5000, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScrapeAllPeersAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "NetworkActivitySyncWorker: error during peer scrape");
            }

            await Task.Delay(DelayMs, stoppingToken);
        }
    }

    private async Task ScrapeAllPeersAsync(CancellationToken ct)
    {
        var slimFaasPods = replicasService.Deployments?.SlimFaas?.Pods;
        if (slimFaasPods == null || slimFaasPods.Count <= 1)
            return; // Single node — nothing to sync

        string baseFunctionPodUrl = slimFaasOptions.Value.BaseFunctionPodUrl;
        string ns = namespaceProvider.CurrentNamespace;
        string myNodeId = tracker.NodeId;

        var client = httpClientFactory.CreateClient("ActivitySync");
        client.Timeout = TimeSpan.FromSeconds(3);

        foreach (var pod in slimFaasPods)
        {
            if (pod.Ready is not true || string.IsNullOrEmpty(pod.Ip))
                continue;

            // Skip self
            if (!string.IsNullOrEmpty(pod.Name) && pod.Name.Contains(myNodeId, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string baseUrl = SlimDataEndpoint.Get(pod, baseFunctionPodUrl, ns);
                // Strip trailing path to get the host:port base
                var uri = new Uri(baseUrl);
                string peerBase = $"{uri.Scheme}://{uri.Authority}";

                _peerLastTimestamp.TryGetValue(peerBase, out long since);

                string url = $"{peerBase}/internal/activity-events?since={since}";
                var response = await client.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                    continue;

                var json = await response.Content.ReadAsStringAsync(ct);
                var events = JsonSerializer.Deserialize(json,
                    StatusStreamSerializerContext.Default.ListNetworkActivityEvent);

                if (events == null || events.Count == 0)
                    continue;

                int ingested = tracker.IngestRemote(events);

                // Update watermark to the max timestamp from this peer
                long maxTs = since;
                foreach (var e in events)
                {
                    if (e.TimestampMs > maxTs)
                        maxTs = e.TimestampMs;
                }
                _peerLastTimestamp[peerBase] = maxTs;

                if (ingested > 0)
                {
                    logger.LogDebug("NetworkActivitySyncWorker: ingested {Count} events from {Peer}",
                        ingested, peerBase);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "NetworkActivitySyncWorker: failed to scrape peer {PodName}", pod.Name);
            }
        }
    }
}

