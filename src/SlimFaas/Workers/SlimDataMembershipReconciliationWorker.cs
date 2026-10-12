using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;
using SlimData;
using SlimData.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public sealed class SlimDataMembershipReconciliationWorker(
    IReplicasService replicasService,
    IRaftHttpCluster cluster,
    IClusterMembershipCoordinator membershipCoordinator,
    ILogger<SlimDataMembershipReconciliationWorker> logger,
    IOptions<SlimFaasOptions> slimFaasOptions,
    IOptions<WorkersOptions> workersOptions,
    IOptions<SlimDataMembershipOptions> membershipOptions,
    INamespaceProvider namespaceProvider)
    : BackgroundService
{
    private readonly int _delay = workersOptions.Value.ReplicasSynchronizationDelayMilliseconds;
    private readonly int _removalMissingCycles = membershipOptions.Value.RemovalMissingCycles;
    private readonly string _baseSlimDataUrl = slimFaasOptions.Value.BaseSlimDataUrl;
    private readonly string _namespace = namespaceProvider.CurrentNamespace;
    private readonly Dictionary<MembershipEndpointKey, int> _missingCycles = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogSlimDataMembershipReconciliationWorkerStart();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_delay, stoppingToken).ConfigureAwait(false);
                await ReconcileOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogErrorInSlimDataMembershipReconciliationWorker(ex);
            }
        }
    }

    internal async Task ReconcileOnceAsync(CancellationToken token)
    {
        if (!HasActiveLeadership())
        {
            _missingCycles.Clear();
            return;
        }

        SlimFaasDeploymentInformation topology = replicasService.Deployments.SlimFaas;
        Dictionary<MembershipEndpointKey, Uri> desired = GetDesiredMembers(topology.Pods);
        Dictionary<MembershipEndpointKey, Uri> current = GetCurrentMembers();
        var localKey = MembershipEndpointKey.From(cluster.LocalMemberAddress);
        bool completeTopology = topology.Replicas > 0 && desired.Count == topology.Replicas;

        // A pod can disappear or lose its IP/Started status while the StatefulSet
        // still requires it. Such an observation must not change the Raft quorum.
        if (!completeTopology)
        {
            _missingCycles.Clear();
            logger.LogDeferringSlimDataMembershipRemovals(topology.Replicas, desired.Count);
        }

        ResetObservedMembers(desired, current);

        Uri? memberToAdd = desired
            .Where(pair => !current.ContainsKey(pair.Key))
            .OrderBy(static pair => pair.Value.AbsoluteUri, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .FirstOrDefault();
        if (memberToAdd is not null)
        {
            _missingCycles.Clear();
            logger.LogAddingMissingSlimDataRaftMemberEndpoint(memberToAdd);
            if (!await membershipCoordinator.AddMemberAsync(memberToAdd, token).ConfigureAwait(false))
            {
                logger.LogSlimDataRaftMemberWasNotAdded(memberToAdd);
            }

            return;
        }

        if (!desired.ContainsKey(localKey))
        {
            _missingCycles.Clear();
            logger.LogSkippingSlimDataMembershipRemovalsBecauseThe(cluster.LocalMemberAddress);
            return;
        }

        if (!completeTopology)
            return;

        KeyValuePair<MembershipEndpointKey, Uri>[] staleMembers = current
            .Where(pair => pair.Key != localKey && !desired.ContainsKey(pair.Key))
            .OrderBy(static pair => pair.Value.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        Dictionary<MembershipEndpointKey, int> previousMissingCycles = staleMembers.ToDictionary(
            static pair => pair.Key,
            pair => _missingCycles.GetValueOrDefault(pair.Key));
        foreach (KeyValuePair<MembershipEndpointKey, Uri> stale in staleMembers)
            _missingCycles[stale.Key] = _missingCycles.GetValueOrDefault(stale.Key) + 1;

        KeyValuePair<MembershipEndpointKey, Uri> memberToRemove = staleMembers.FirstOrDefault(pair =>
            _missingCycles.GetValueOrDefault(pair.Key) >= _removalMissingCycles);
        if (memberToRemove.Value is null)
            return;

        try
        {
            logger.LogRemovingStaleSlimDataRaftMemberEndpoint(memberToRemove.Value, _missingCycles[memberToRemove.Key]);
            if (await membershipCoordinator.RemoveMemberAsync(memberToRemove.Value, token).ConfigureAwait(false))
            {
                _missingCycles.Remove(memberToRemove.Key);
            }
            else
            {
                RestoreMissingCycles(previousMissingCycles);
                logger.LogSlimDataRaftMemberWasNotRemoved(memberToRemove.Value);
            }
        }
        catch
        {
            RestoreMissingCycles(previousMissingCycles);
            throw;
        }
    }

    private bool HasActiveLeadership()
    {
        try
        {
            return !cluster.LeadershipToken.IsCancellationRequested &&
                   !cluster.ConsensusToken.IsCancellationRequested &&
                   cluster.TryGetLeaseToken(out CancellationToken leaseToken) &&
                   !leaseToken.IsCancellationRequested;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Dictionary<MembershipEndpointKey, Uri> GetDesiredMembers(IEnumerable<PodInformation> pods)
    {
        var result = new Dictionary<MembershipEndpointKey, Uri>();
        foreach (PodInformation pod in pods
                     .Where(static pod => pod.Started is true && !string.IsNullOrWhiteSpace(pod.Ip)))
        {
            try
            {
                var endpoint = new Uri(SlimDataEndpoint.Get(pod, _baseSlimDataUrl, _namespace));
                result[MembershipEndpointKey.From(endpoint)] = endpoint;
            }
            catch (UriFormatException ex)
            {
                logger.LogIgnoringInvalidSlimDataEndpointForPod(ex, pod.Name);
            }
        }

        return result;
    }

    private Dictionary<MembershipEndpointKey, Uri> GetCurrentMembers()
    {
        var result = new Dictionary<MembershipEndpointKey, Uri>();
        foreach (IRaftClusterMember member in ((IRaftCluster)cluster).Members)
        {
            if (member.EndPoint is not UriEndPoint endpoint)
            {
                logger.LogIgnoringSlimDataRaftMemberWithoutAn(member.EndPoint);
                continue;
            }

            result[MembershipEndpointKey.From(endpoint.Uri)] = endpoint.Uri;
        }

        return result;
    }

    private void ResetObservedMembers(
        Dictionary<MembershipEndpointKey, Uri> desired,
        Dictionary<MembershipEndpointKey, Uri> current)
    {
        foreach (MembershipEndpointKey endpoint in _missingCycles.Keys.ToArray())
        {
            if (desired.ContainsKey(endpoint) || !current.ContainsKey(endpoint))
                _missingCycles.Remove(endpoint);
        }
    }

    private void RestoreMissingCycles(
        IReadOnlyDictionary<MembershipEndpointKey, int> previousMissingCycles)
    {
        foreach ((MembershipEndpointKey endpoint, int count) in previousMissingCycles)
        {
            if (count == 0)
                _missingCycles.Remove(endpoint);
            else
                _missingCycles[endpoint] = count;
        }
    }

    internal readonly record struct MembershipEndpointKey(string Scheme, string Host, int Port)
    {
        internal static MembershipEndpointKey From(Uri endpoint)
            => new(
                endpoint.Scheme.ToLowerInvariant(),
                endpoint.IdnHost.ToLowerInvariant(),
                endpoint.Port);
    }
}
