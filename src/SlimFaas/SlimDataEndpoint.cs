using SlimFaas.Kubernetes;

namespace SlimFaas;

public class SlimDataEndpoint
{
    public static string Get(PodInformation podInformation, string? baseUrl = null)
    {
        string baseSlimDataUrl = baseUrl ?? Environment.GetEnvironmentVariable(EnvironmentVariables.BaseSlimDataUrl) ??
                                 EnvironmentVariables.BaseSlimDataUrlDefault;
        string namespaceSlimFaas = Environment.GetEnvironmentVariable(EnvironmentVariables.Namespace) ?? EnvironmentVariables.NamespaceDefault;
        if (!string.IsNullOrEmpty(baseSlimDataUrl))
        {
            baseSlimDataUrl = baseSlimDataUrl.Replace("{pod_name}", podInformation.Name);
            baseSlimDataUrl = baseSlimDataUrl.Replace("{pod_ip}", podInformation.Ip);
            baseSlimDataUrl = baseSlimDataUrl.Replace("{service_name}", podInformation.ServiceName ?? "slimfaas");
            var ports = podInformation.Ports;
            if (ports != null)
            {
                if (ports.Count > 0)
                {
                    baseSlimDataUrl = baseSlimDataUrl.Replace("{pod_port}", ports[0].ToString());
                }
                foreach (int port in ports)
                {
                    var index = ports.IndexOf(port);
                    baseSlimDataUrl = baseSlimDataUrl.Replace($"{{pod_port_{index}}}", port.ToString());
                }
            }

            baseSlimDataUrl = baseSlimDataUrl.Replace("{namespace}", namespaceSlimFaas);
            baseSlimDataUrl = baseSlimDataUrl.Replace("{function_name}", podInformation.DeploymentName);
        }

        return baseSlimDataUrl;
    }
}
