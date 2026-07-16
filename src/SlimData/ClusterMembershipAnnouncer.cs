using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using SlimData.Options;

namespace SlimData;

internal sealed class ClusterMembershipAnnouncer(
    IHttpClientFactory httpClientFactory,
    IOptions<SlimDataMembershipOptions> options,
    ILogger<ClusterMembershipAnnouncer> logger)
{
    internal const string HttpClientName = "SlimDataMembershipAnnouncer";
    private readonly TimeSpan _attemptTimeout = TimeSpan.FromSeconds(options.Value.AnnouncementTimeoutSeconds);

    internal async Task AnnounceAsync(
        UriEndPoint address,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken token)
    {
        foreach (var candidate in Startup.GetKnownClusterMembers())
        {
            if (Startup.SameEndpoint(candidate, address.Uri))
                continue;

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(_attemptTimeout);
            try
            {
                if (await TryAnnounceAsync(candidate, address.Uri, attempt.Token).ConfigureAwait(false))
                    return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                logger.LogDebug("Membership announcement to {Candidate} timed out", candidate);
            }
            catch (HttpRequestException ex)
            {
                logger.LogDebug(ex, "Membership announcement to {Candidate} failed", candidate);
            }
        }
    }

    private async Task<bool> TryAnnounceAsync(Uri candidate, Uri localAddress, CancellationToken token)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        var target = BuildUri(candidate, localAddress);
        for (var redirectCount = 0; redirectCount < 2; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            if (response.Headers.Location is not { } location)
                return false;

            target = location.IsAbsoluteUri ? location : new Uri(target, location);
        }

        return false;
    }

    private static Uri BuildUri(Uri candidate, Uri localAddress)
    {
        var builder = new UriBuilder(candidate)
        {
            Path = Startup.MembershipAnnounceResource,
            Query = $"endpoint={Uri.EscapeDataString(localAddress.AbsoluteUri)}"
        };
        return builder.Uri;
    }
}

internal sealed class ClusterMembershipAnnounceWorker(
    IRaftHttpCluster cluster,
    ClusterMembershipAnnouncer announcer,
    ILogger<ClusterMembershipAnnounceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                if (!cluster.ConsensusToken.IsCancellationRequested)
                    continue;

                await announcer.AnnounceAsync(
                    new UriEndPoint(cluster.LocalMemberAddress),
                    new Dictionary<string, string>(),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to announce this SlimData member to the cluster");
            }
        }
    }
}
