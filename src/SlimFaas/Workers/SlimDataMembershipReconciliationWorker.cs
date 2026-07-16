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
        logger.LogInformation("SlimDataMembershipReconciliationWorker: Start");
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
                logger.LogError(ex, "Error in SlimDataMembershipReconciliationWorker");
            }
        }
    }

    internal async Task ReconcileOnceAsync(CancellationToken token)
    {
        if (!HasActiveLeadership())
            return;

        var desired = GetDesiredMembers();
        var current = GetCurrentMembers();
        var localKey = MembershipEndpointKey.From(cluster.LocalMemberAddress);

        ResetObservedMembers(desired, current);

        var memberToAdd = desired
            .Where(pair => !current.ContainsKey(pair.Key))
            .OrderBy(static pair => pair.Value.AbsoluteUri, StringComparer.Ordinal)
            .Select(static pair => pair.Value)
            .FirstOrDefault();
        if (memberToAdd is not null)
        {
            logger.LogInformation(
                "Adding missing SlimData Raft member. Endpoint={Endpoint}",
                memberToAdd);
            if (!await membershipCoordinator.AddMemberAsync(memberToAdd, token).ConfigureAwait(false))
            {
                logger.LogWarning(
                    "SlimData Raft member was not added and will be retried. Endpoint={Endpoint}",
                    memberToAdd);
            }

            return;
        }

        if (!desired.ContainsKey(localKey))
        {
            logger.LogWarning(
                "Skipping SlimData membership removals because the local endpoint is absent from the orchestrator snapshot. LocalEndpoint={LocalEndpoint}",
                cluster.LocalMemberAddress);
            return;
        }

        var staleMembers = current
            .Where(pair => pair.Key != localKey && !desired.ContainsKey(pair.Key))
            .OrderBy(static pair => pair.Value.AbsoluteUri, StringComparer.Ordinal)
            .ToArray();
        var previousMissingCycles = staleMembers.ToDictionary(
            static pair => pair.Key,
            pair => _missingCycles.GetValueOrDefault(pair.Key));
        foreach (var stale in staleMembers)
            _missingCycles[stale.Key] = _missingCycles.GetValueOrDefault(stale.Key) + 1;

        var memberToRemove = staleMembers.FirstOrDefault(pair =>
            _missingCycles.GetValueOrDefault(pair.Key) >= _removalMissingCycles);
        if (memberToRemove.Value is null)
            return;

        try
        {
            logger.LogInformation(
                "Removing stale SlimData Raft member. Endpoint={Endpoint}, MissingCycles={MissingCycles}",
                memberToRemove.Value,
                _missingCycles[memberToRemove.Key]);
            if (await membershipCoordinator.RemoveMemberAsync(memberToRemove.Value, token).ConfigureAwait(false))
            {
                _missingCycles.Remove(memberToRemove.Key);
            }
            else
            {
                RestoreMissingCycles(previousMissingCycles);
                logger.LogWarning(
                    "SlimData Raft member was not removed and will be retried. Endpoint={Endpoint}",
                    memberToRemove.Value);
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
                   cluster.TryGetLeaseToken(out var leaseToken) &&
                   !leaseToken.IsCancellationRequested;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private Dictionary<MembershipEndpointKey, Uri> GetDesiredMembers()
    {
        var result = new Dictionary<MembershipEndpointKey, Uri>();
        foreach (var pod in replicasService.Deployments.SlimFaas.Pods
                     .Where(static pod => pod.Started is true && !string.IsNullOrWhiteSpace(pod.Ip)))
        {
            try
            {
                var endpoint = new Uri(SlimDataEndpoint.Get(pod, _baseSlimDataUrl, _namespace));
                result[MembershipEndpointKey.From(endpoint)] = endpoint;
            }
            catch (UriFormatException ex)
            {
                logger.LogWarning(ex, "Ignoring invalid SlimData endpoint for pod {PodName}", pod.Name);
            }
        }

        return result;
    }

    private Dictionary<MembershipEndpointKey, Uri> GetCurrentMembers()
    {
        var result = new Dictionary<MembershipEndpointKey, Uri>();
        foreach (var member in ((IRaftCluster)cluster).Members)
        {
            if (member.EndPoint is not UriEndPoint endpoint)
            {
                logger.LogWarning("Ignoring SlimData Raft member without an HTTP endpoint. Endpoint={Endpoint}", member.EndPoint);
                continue;
            }

            result[MembershipEndpointKey.From(endpoint.Uri)] = endpoint.Uri;
        }

        return result;
    }

    private void ResetObservedMembers(
        IReadOnlyDictionary<MembershipEndpointKey, Uri> desired,
        IReadOnlyDictionary<MembershipEndpointKey, Uri> current)
    {
        foreach (var endpoint in _missingCycles.Keys.ToArray())
        {
            if (desired.ContainsKey(endpoint) || !current.ContainsKey(endpoint))
                _missingCycles.Remove(endpoint);
        }
    }

    private void RestoreMissingCycles(
        IReadOnlyDictionary<MembershipEndpointKey, int> previousMissingCycles)
    {
        foreach (var (endpoint, count) in previousMissingCycles)
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
