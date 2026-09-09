using Microsoft.AspNetCore.Connections;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Endpoints;

public sealed class StatusLeader(IRaftCluster cluster, IOptions<SlimFaasOptions> options,
    INamespaceProvider namespaceProvider)
{
    public string? FindLeader(IList<PodInformation> pods) => FindLeader(
        (cluster.Leader?.EndPoint as UriEndPoint)?.Uri, pods,
        options.Value.BaseSlimDataUrl, namespaceProvider.CurrentNamespace);

    internal static string? FindLeader(Uri? leader, IList<PodInformation> pods, string baseUrl, string ns)
    {
        if (leader is null) return null;
        var matches = pods.Where(p => Uri.TryCreate(SlimDataEndpoint.Get(p, baseUrl, ns), UriKind.Absolute, out var endpoint)
            && Uri.Compare(endpoint, leader, UriComponents.SchemeAndServer | UriComponents.Path,
                UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0).ToArray();
        return matches.Length == 1 ? matches[0].Name : null;
    }
}
