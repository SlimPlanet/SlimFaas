using System.Reflection;
using k8s;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaas.Kubernetes;

namespace SlimFaas.Tests.Kubernetes;

internal static class KubernetesTestService
{
    internal static KubernetesService Create(
        DelegatingHandler handler,
        out k8s.Kubernetes client)
    {
        var config = new KubernetesClientConfiguration { Host = "http://localhost" };
        client = new k8s.Kubernetes(config, handler);

        var service = (KubernetesService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(KubernetesService));

        typeof(KubernetesService)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, client);
        typeof(KubernetesService)
            .GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, NullLogger<KubernetesService>.Instance);

        return service;
    }
}
