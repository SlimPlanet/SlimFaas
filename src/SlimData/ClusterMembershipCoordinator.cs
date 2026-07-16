using System.Diagnostics;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using SlimData.Options;

namespace SlimData;

public sealed class ClusterMembershipCoordinator(
    IRaftHttpCluster cluster,
    IOptions<SlimDataMembershipOptions> options,
    ILogger<ClusterMembershipCoordinator> logger)
{
    private readonly SemaphoreSlim _membershipGate = new(1, 1);
    private readonly TimeSpan _changeTimeout = TimeSpan.FromSeconds(options.Value.ChangeTimeoutSeconds);

    public Task<bool> AddMemberAsync(Uri endpoint, CancellationToken token)
        => ChangeMembershipAsync(endpoint, add: true, token);

    public Task<bool> RemoveMemberAsync(Uri endpoint, CancellationToken token)
        => ChangeMembershipAsync(endpoint, add: false, token);

    private async Task<bool> ChangeMembershipAsync(Uri endpoint, bool add, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_changeTimeout);
        var lockTaken = false;
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            await _membershipGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            lockTaken = true;

            var isMember = IsMember(endpoint);
            if ((add && isMember) || (!add && !isMember))
                return true;

            logger.LogInformation(
                "Starting SlimData membership {Operation}. Endpoint={Endpoint}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}, TimeoutSeconds={TimeoutSeconds}",
                add ? "addition" : "removal",
                endpoint,
                cluster.AuditTrail.LastEntryIndex,
                cluster.AuditTrail.LastCommittedEntryIndex,
                _changeTimeout.TotalSeconds);

            var changed = add
                ? await cluster.AddMemberAsync(endpoint, timeout.Token).ConfigureAwait(false)
                : await cluster.RemoveMemberAsync(endpoint, timeout.Token).ConfigureAwait(false);

            logger.Log(
                changed ? LogLevel.Information : LogLevel.Warning,
                "SlimData membership {Operation} completed. Endpoint={Endpoint}, Applied={Applied}, DurationMilliseconds={DurationMilliseconds}, LastLogIndex={LastLogIndex}, CommittedLogIndex={CommittedLogIndex}",
                add ? "addition" : "removal",
                endpoint,
                changed,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                cluster.AuditTrail.LastEntryIndex,
                cluster.AuditTrail.LastCommittedEntryIndex);
            return changed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            logger.LogWarning(
                "SlimData membership {Operation} timed out. Endpoint={Endpoint}, TimeoutSeconds={TimeoutSeconds}, DurationMilliseconds={DurationMilliseconds}",
                add ? "addition" : "removal",
                endpoint,
                _changeTimeout.TotalSeconds,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
        finally
        {
            if (lockTaken)
                _membershipGate.Release();
        }
    }

    private bool IsMember(Uri endpoint)
    {
        if (Startup.SameEndpoint(cluster.LocalMemberAddress, endpoint))
            return true;

        return ((IRaftCluster)cluster).Members.Any(member =>
            member.EndPoint is UriEndPoint address && Startup.SameEndpoint(address.Uri, endpoint));
    }
}
