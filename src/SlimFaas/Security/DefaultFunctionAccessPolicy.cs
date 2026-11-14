using System.Net;
using Microsoft.Extensions.Options;
using SlimFaas.Jobs;
using SlimFaas.Kubernetes;
using SlimFaas.Local;
using SlimFaas.Options;
using SlimFaas.WebSocket;

namespace SlimFaas.Security;

public sealed class DefaultFunctionAccessPolicy(
    IReplicasService replicasService,
    IJobService jobService,
    IWebSocketFunctionRepository webSocketFunctionRepository,
    IOptions<SlimFaasOptions> options,
    ILogger<DefaultFunctionAccessPolicy> logger)
    : IFunctionAccessPolicy
{
    private static readonly object s_internalCacheKey = new(); // cache par requête (HttpContext.Items)
    private static readonly object s_callerIdentityCacheKey = new();

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

    /// <summary>
    /// Identifies the workload behind a request, for <c>SlimFaas/AllowedCallers</c>: the
    /// name of the function whose pod owns the connection address, or the job
    /// configuration name of the job pod that owns it. Native local workloads share the
    /// loopback address, so they are identified only by the identity their workload
    /// gateway signs with the process token. Returns <c>null</c> when no workload owns
    /// the address or when several workloads share it.
    /// </summary>
    public string? ResolveCallerIdentity(HttpContext context)
    {
        if (context.Items.TryGetValue(s_callerIdentityCacheKey, out var cached))
            return cached as string;

        string? identity = ResolveSignedLocalIdentity(context)
                           ?? ResolveAddressIdentity(context.Connection.RemoteIpAddress);

        context.Items[s_callerIdentityCacheKey] = identity;
        return identity;
    }

    private string? ResolveSignedLocalIdentity(HttpContext context)
    {
        string token = options.Value.Process.Token;
        if (string.IsNullOrEmpty(token))
            return null;

        IHeaderDictionary headers = context.Request.Headers;
        string jobName = headers[LocalWorkloadGateway.JobHeaderName].ToString().Trim();
        if (jobName.Length > 0 &&
            LocalWorkloadGateway.IsSignatureValid(
                jobName, token, headers[LocalWorkloadGateway.SignatureHeaderName].ToString().Trim()))
        {
            return JobConfigurationName(jobName);
        }

        string podName = headers[LocalWorkloadGateway.PodHeaderName].ToString().Trim();
        if (podName.Length > 0 &&
            LocalWorkloadGateway.IsPodSignatureValid(
                podName, token, headers[LocalWorkloadGateway.PodSignatureHeaderName].ToString().Trim()))
        {
            return replicasService.Deployments.Functions
                .FirstOrDefault(f => f.Pods.Any(p => string.Equals(p.Name, podName, StringComparison.Ordinal)))?
                .Deployment;
        }

        return null;
    }

    private string? ResolveAddressIdentity(IPAddress? remote)
    {
        // A loopback address is shared by every native local workload and by local tools.
        if (remote is null || IPAddress.IsLoopback(RemoteAddress.Normalize(remote)))
            return null;

        DeploymentsInformations deployments = replicasService.Deployments;
        if (RemoteAddress.IsAnyOf(remote, deployments.SlimFaas.Pods.Select(p => p.Ip)))
            return null; // A SlimFaas pod, or a workload sharing its address, is neither a function nor a job.

        IEnumerable<string> owners = deployments.Functions
            .Where(f => RemoteAddress.IsAnyOf(remote, f.Pods.Select(p => p.Ip)))
            .Select(f => f.Deployment)
            .Concat(jobService.Jobs
                .Where(j => RemoteAddress.IsAnyOf(remote, j.Ips))
                .Select(j => JobConfigurationName(j.Name)));

        string? identity = null;
        foreach (string owner in owners)
        {
            if (identity is not null && !string.Equals(identity, owner, StringComparison.Ordinal))
                return null; // Shared host-network address: the caller cannot be told apart.
            identity = owner;
        }

        return identity;
    }

    private static string JobConfigurationName(string jobName)
    {
        int separator = jobName.LastIndexOf(KubernetesService.SlimfaasJobKey, StringComparison.Ordinal);
        return separator > 0 ? jobName[..separator] : jobName;
    }

    public bool IsAllowedCaller(HttpContext context, DeploymentInformation function)
    {
        if (function.AllowedCallers is not { Count: > 0 } allowedCallers)
            return true;

        string? caller = ResolveCallerIdentity(context);
        if (caller is not null && allowedCallers.Any(pattern => MatchesCallerPattern(pattern, caller)))
            return true;

        logger.LogCallerNotAllowed(caller ?? "unknown", context.Connection.RemoteIpAddress?.ToString(), function.Deployment);
        return false;
    }

    /// <summary>
    /// Ordinal comparison where <c>*</c> matches any sequence of characters, including none.
    /// </summary>
    internal static bool MatchesCallerPattern(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        int p = 0, v = 0, star = -1, starValue = 0;
        while (v < value.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starValue = v;
            }
            else if (p < pattern.Length && pattern[p] == value[v])
            {
                p++;
                v++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++starValue;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

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

            if (IsInternalRequest(context) && IsAllowedCaller(context, deployment))
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

            if (IsInternalRequest(context) && IsAllowedCaller(context, deployment))
                result.Add(deployment);
        }

        return result;
    }
}
