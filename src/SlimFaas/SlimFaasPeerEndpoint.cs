using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

internal static class SlimFaasPeerEndpoint
{
    internal static Uri? Resolve(PodInformation pod, SlimFaasOptions options, string ns)
    {
        var applicationPod = pod with { EndpointUrl = null };
        if (options.BaseFunctionPodUrl.Contains("{pod_port}", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(SlimDataEndpoint.Get(pod, options.BaseSlimDataUrl, ns), UriKind.Absolute, out var raft)) return null;
            var ports = pod.Ports?.Where(port => port != raft.Port).Distinct().ToList();
            if (ports is not { Count: > 0 }) return null;
            applicationPod = applicationPod with { Ports = ports };
        }
        return Uri.TryCreate(SlimDataEndpoint.Get(applicationPod, options.BaseFunctionPodUrl, ns), UriKind.Absolute, out var endpoint)
            ? endpoint : null;
    }
}
