using Microsoft.Extensions.Primitives;
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


    public bool IsInternalRequest(HttpContext context)
    {
        if (context.Items.TryGetValue(s_internalCacheKey, out var cached) && cached is bool b)
            return b;

        var trustedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pods des fonctions "Trusted"
        foreach (var ip in replicasService.Deployments.Functions
                     .Where(f => f.Trust == FunctionTrust.Trusted)
                     .SelectMany(f => f.Pods)
                     .Select(p => p.Ip)
                     .Where(ip => !string.IsNullOrWhiteSpace(ip)))
        {
            trustedIps.Add(ip!);
        }

        // IPs des jobs
        foreach (var ip in jobService.Jobs.SelectMany(j => j.Ips).Where(ip => !string.IsNullOrWhiteSpace(ip)))
        {
            trustedIps.Add(ip!);
        }

        // IP candidates
        var candidates = new List<string>(capacity: 8);

        var remoteIp = context.Connection.RemoteIpAddress?.ToString();
        if (!string.IsNullOrWhiteSpace(remoteIp)) candidates.Add(NormalizeIp(remoteIp!));

        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out StringValues xff))
        {
            foreach (var token in SplitForwardedFor(xff))
                candidates.Add(NormalizeIp(token));
        }

        var isInternal = candidates.Any(c => MatchesTrusted(c, trustedIps));

        logger.LogDebug("IsInternalRequest={IsInternal} Remote={RemoteIp} XFF={Xff}",
            isInternal, remoteIp, context.Request.Headers["X-Forwarded-For"].FirstOrDefault() ?? "");

        context.Items[s_internalCacheKey] = isInternal;
        return isInternal;
    }

    public FunctionVisibility ResolveVisibility(DeploymentInformation function, string path)
    {
        if (function.PathsStartWithVisibility is not { Count: > 0 })
            return function.Visibility;

        var p = (path ?? string.Empty).ToLowerInvariant();

        foreach (var rule in function.PathsStartWithVisibility)
        {
            if (string.IsNullOrWhiteSpace(rule.Path))
                continue;

            if (p.StartsWith(rule.Path.ToLowerInvariant()))
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

    private static IEnumerable<string> SplitForwardedFor(StringValues xff)
    {
        // "client, proxy1, proxy2"
        foreach (var raw in xff)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
        }
    }

    private static string NormalizeIp(string ip)
    {
        // gère ::ffff:10.0.0.1
        if (ip.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
            return ip["::ffff:".Length..];

        // enlève un port éventuel "10.0.0.1:1234" (IPv4)
        var idx = ip.LastIndexOf(':');
        if (idx > 0 && ip.Count(c => c == ':') == 1)
            return ip[..idx];

        return ip;
    }

    private static bool MatchesTrusted(string candidate, HashSet<string> trustedIps)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        if (trustedIps.Contains(candidate))
            return true;

        // fallback “compat” avec ton ancien Contains
        foreach (var ip in trustedIps)
        {
            if (candidate.Contains(ip, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
