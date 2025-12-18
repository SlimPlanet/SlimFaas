using System.Net;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SlimFaas;

public sealed class RaftLeaderPublicPortRedirectFilter : IEndpointFilter
{
    private readonly IRaftCluster _cluster;
    private readonly ISlimFaasPorts _ports;
    private readonly ILogger<RaftLeaderPublicPortRedirectFilter> _logger;

    public RaftLeaderPublicPortRedirectFilter(
        IRaftCluster cluster,
        ISlimFaasPorts ports,
        ILogger<RaftLeaderPublicPortRedirectFilter> logger)
    {
        _cluster = cluster;
        _ports = ports;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Fast path: local node is leader
        if (_cluster.TryGetLeaseToken(out _))
            return await next(context).ConfigureAwait(false);

        var leader = _cluster.Leader;
        if (leader is null)
        {
            _logger.LogWarning("No RAFT leader known (Leader is null). Returning 503.");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        if (!TryBuildBaseUriFromEndPoint(leader.EndPoint, out var leaderBaseUri))
        {
            _logger.LogWarning("Unable to build leader URI from endpoint type {EndPointType}. Returning 503.",
                leader.EndPoint?.GetType().FullName ?? "<null>");
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var http = context.HttpContext;

        // leaderBaseUri.Port == RAFT port in your setup
        var applicationPort = SelectApplicationPort(
            http,
            raftPort: leaderBaseUri.Port,
            allPorts: _ports.Ports ?? Array.Empty<int>());

        if (applicationPort <= 0)
        {
            _logger.LogWarning("No suitable public port found (raftPort={RaftPort}). Returning 503.", leaderBaseUri.Port);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var redirectUri = BuildRedirectUri(http, leaderBaseUri, applicationPort);

        _logger.LogInformation("Redirecting to leader (public port). From {From} to {To}",
            http.Request.Path + http.Request.QueryString, redirectUri);

        // 307 => preserve method (POST/PUT/DELETE)
        return Results.Redirect(redirectUri.ToString(), permanent: false, preserveMethod: true);
    }

    private static int SelectApplicationPort(HttpContext http, int raftPort, IList<int> allPorts)
    {
        // Prefer the incoming port if it's a known SlimFaas port AND it's not RAFT.
        var incomingPort = http.Request.Host.Port ?? http.Connection.LocalPort;
        if (incomingPort > 0 && incomingPort != raftPort && allPorts.Contains(incomingPort))
            return incomingPort;

        // Otherwise pick the first configured port that isn't RAFT.
        foreach (var p in allPorts)
        {
            if (p > 0 && p != raftPort)
                return p;
        }

        // Last fallback: if incoming is non-raft but not in list, still use it.
        if (incomingPort > 0 && incomingPort != raftPort)
            return incomingPort;

        return 0;
    }

    private static Uri BuildRedirectUri(HttpContext http, Uri leaderBaseUri, int applicationPort)
    {
        var path = http.Request.PathBase.Add(http.Request.Path).Value;
        if (string.IsNullOrEmpty(path))
            path = "/";

        var qb = http.Request.QueryString;
        var query = qb.HasValue ? qb.Value![1..] : string.Empty; // UriBuilder.Query must not include '?'

        var ub = new UriBuilder(leaderBaseUri)
        {
            Port = applicationPort,
            Path = path,
            Query = query
        };

        return ub.Uri;
    }

    private static bool TryBuildBaseUriFromEndPoint(EndPoint? endPoint, out Uri uri)
    {
        uri = default!;

        if (endPoint is null)
            return false;

        // DotNext HTTP endpoint
        if (endPoint is HttpEndPoint hep)
        {
            uri = hep.CreateUriBuilder().Uri;
            return true;
        }

        // ASP.NET Core UriEndPoint
        if (endPoint is UriEndPoint uep)
        {
            uri = uep.Uri;
            return uri.IsAbsoluteUri;
        }

        // Classic endpoints
        if (endPoint is DnsEndPoint dep)
        {
            uri = new UriBuilder(Uri.UriSchemeHttp, dep.Host, dep.Port).Uri;
            return true;
        }

        if (endPoint is IPEndPoint ip)
        {
            uri = new UriBuilder(Uri.UriSchemeHttp, ip.Address.ToString(), ip.Port).Uri;
            return true;
        }

        return false;
    }
}

internal static class PortListExtensions
{
    public static bool Contains(this IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return true;
        return false;
    }
}


public static class EndpointFilterExtensions
{
    /// <summary>
    /// Ajoute une redirection 307 vers le leader RAFT, mais en remappant le port vers un port public SlimFaas.
    /// À mettre sur les endpoints "write" (POST/PUT/DELETE).
    /// </summary>
    public static RouteHandlerBuilder RedirectToLeaderOnPublicPort(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<RaftLeaderPublicPortRedirectFilter>();
}
