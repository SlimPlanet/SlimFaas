﻿using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas;

public interface ISlimFaasPorts
{
    IList<int> Ports { get; }
}

public class SlimFaasPorts : ISlimFaasPorts
{
    public IList<int> Ports { get; }

    public SlimFaasPorts(IReplicasService replicasService, ILogger<SlimFaasPorts> logger, IOptions<SlimFaasOptions> slimFaasOptions)
    {
        var options = slimFaasOptions.Value;
        Ports = ResolvePorts(
            replicasService.Deployments,
            options.BaseSlimDataUrl,
            options.Namespace,
            Environment.GetEnvironmentVariable("HOSTNAME") ?? Dns.GetHostName(),
            logger);
    }

    internal static IList<int> ResolvePorts(
        DeploymentsInformations deployments,
        string baseSlimDataUrl,
        string kubeNamespace,
        string hostname,
        ILogger logger)
    {
        var currentPod = deployments.SlimFaas.Pods
            .FirstOrDefault(pod => pod.Name.Contains(hostname, StringComparison.Ordinal))
            ?? deployments.SlimFaas.Pods.FirstOrDefault();
        var ports = currentPod?.Ports;
        if (currentPod is null || ports is null)
        {
            logger.LogWarning("SlimFaas no ports found");
            return [];
        }

        string slimDataEndpoint = SlimDataEndpoint.Get(currentPod, baseSlimDataUrl, kubeNamespace);
        if (!Uri.TryCreate(slimDataEndpoint, UriKind.Absolute, out var slimDataUri))
        {
            throw new InvalidOperationException(
                $"Unable to resolve SlimData endpoint '{slimDataEndpoint}' for pod '{currentPod.Name}'.");
        }

        var result = ports.Where(port => port != slimDataUri.Port).Distinct().ToArray();
        foreach (int port in result)
        {
            logger.LogInformation("SlimFaasPorts: {Port}", port);
        }

        return result;
    }
    public static string RemoveLastPathSegment(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }
        if (url.EndsWith('/'))
        {
            url = url.Substring(0, url.Length - 1);
        }

        return url;
    }
}
