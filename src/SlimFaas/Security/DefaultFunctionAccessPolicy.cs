using System.Net;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.WebSocket;

namespace SlimFaas.Security;

public sealed class DefaultFunctionAccessPolicy(
    IReplicasService replicasService,
    IJobService jobService,
    IWebSocketFunctionRepository webSocketFunctionRepository,
    ILogger<DefaultFunctionAccessPolicy> logger)
    : IFunctionAccessPolicy
{
    private static readonly object s_internalCacheKey = new(); // cache par requête (HttpContext.Items)

    /// <summary>
    /// A request is internal when the address of its connection is the address of a
    /// Trusted function pod or of a job pod. The <c>X-Forwarded-For</c> header is never
    /// read here: it is only honoured, one hop deep, by the forwarded-headers middleware
    /// for the proxies declared in <c>SlimFaas:TrustedProxies</c>.
    /// </summary>
    public bool IsInternalRequest(HttpContext context)
    {
        if (context.Items.TryGetValue(s_internalCacheKey, out var cached) && cached is bool b)
            return b;

        IPAddress? remote = context.Connection.RemoteIpAddress;

        bool isInternal = RemoteAddress.IsAnyOf(remote, TrustedFunctionPodIps())
                          || RemoteAddress.IsAnyOf(remote, jobService.Jobs.SelectMany(j => j.Ips));

        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogIsInternalRequestRemote(isInternal, remote?.ToString());
        }

        context.Items[s_internalCacheKey] = isInternal;
        return isInternal;
    }

    private IEnumerable<string?> TrustedFunctionPodIps()
        => replicasService.Deployments.Functions
            .Where(f => f.Trust == FunctionTrust.Trusted)
            .SelectMany(f => f.Pods)
            .Select(p => p.Ip);

    public FunctionVisibility ResolveVisibility(DeploymentInformation function, string path)
    {
        if (function.PathsStartWithVisibility is not { Count: > 0 })
            return function.Visibility;

        var p = path ?? string.Empty;

        foreach (var rule in function.PathsStartWithVisibility)
        {
            if (string.IsNullOrWhiteSpace(rule.Path))
                continue;

            if (p.StartsWith(rule.Path, StringComparison.OrdinalIgnoreCase))
                return rule.Visibility;
        }

        return function.Visibility;
    }

    public bool CanAccessFunction(HttpContext context,
        DeploymentInformation function, string path)
    {
        var visibility = ResolveVisibility(function, path);
        if (visibility == FunctionVisibility.Public)
            return true;

        return IsInternalRequest(context);
    }

    public List<DeploymentInformation> GetAllowedSubscribers(HttpContext context, string eventName)
    {
        var result = new List<DeploymentInformation>();

        // Fonctions Kubernetes classiques
        foreach (var deployment in replicasService.Deployments.Functions)
        {
            var sub = deployment.SubscribeEvents?.FirstOrDefault(se => se.Name == eventName);
            if (sub is null) continue;

            if (sub.Visibility == FunctionVisibility.Public)
            {
                result.Add(deployment);
                continue;
            }

            if (IsInternalRequest(context))
                result.Add(deployment);
        }

        // Fonctions WebSocket virtuelles
        foreach (var deployment in webSocketFunctionRepository.GetVirtualDeployments())
        {
            var sub = deployment.SubscribeEvents?.FirstOrDefault(se => se.Name == eventName);
            if (sub is null) continue;

            if (sub.Visibility == FunctionVisibility.Public)
            {
                result.Add(deployment);
                continue;
            }

            if (IsInternalRequest(context))
                result.Add(deployment);
        }

        return result;
    }
}
