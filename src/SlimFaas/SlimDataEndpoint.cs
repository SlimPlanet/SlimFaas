using System.Globalization;
﻿using SlimFaas.Kubernetes;

namespace SlimFaas;

public static class SlimDataEndpoint
{
    public static string Get(PodInformation podInformation, string? baseUrl = null, string? namespaceOverride = null)
    {
        // Note: This method now requires baseUrl and namespace to be passed explicitly
        // They should come from SlimFaasOptions injected in the calling code
        string baseSlimDataUrl = podInformation.EndpointUrl ??
                                 baseUrl ??
                                 "http://{pod_name}.{service_name}.{namespace}.svc:3262";
        string namespaceSlimFaas = namespaceOverride ?? "default";
        if (!string.IsNullOrEmpty(baseSlimDataUrl))
        {
            baseSlimDataUrl = baseSlimDataUrl.Replace("{pod_name}", podInformation.Name, StringComparison.Ordinal);
            baseSlimDataUrl = baseSlimDataUrl.Replace("{pod_ip}", podInformation.Ip, StringComparison.Ordinal);
            baseSlimDataUrl = baseSlimDataUrl.Replace("{service_name}", podInformation.ServiceName ?? "slimfaas", StringComparison.Ordinal);
            var ports = podInformation.Ports;
            if (ports != null)
            {
                if (ports.Count > 0)
                {
                    baseSlimDataUrl = baseSlimDataUrl.Replace("{pod_port}", ports[0].ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                }
                foreach (int port in ports)
                {
                    var index = ports.IndexOf(port);
                    baseSlimDataUrl = baseSlimDataUrl.Replace($"{{pod_port_{index}}}", port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
                }
            }

            baseSlimDataUrl = baseSlimDataUrl.Replace("{namespace}", namespaceSlimFaas, StringComparison.Ordinal);
            baseSlimDataUrl = baseSlimDataUrl.Replace("{function_name}", podInformation.DeploymentName, StringComparison.Ordinal);
        }

        return baseSlimDataUrl;
    }
}
