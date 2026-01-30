
using System.Net.Http.Headers;
using System.Text;
using SlimFaasMcpGateway.Auth;
using SlimFaasMcpGateway.Services;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Gateway;

public interface IGatewayProxyHandler
{
    Task HandleAsync(HttpContext ctx, string tenant, string environment, string configurationName, string? rest, CancellationToken ct);
}

public sealed class GatewayProxyHandler : IGatewayProxyHandler
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailers", "Transfer-Encoding", "Upgrade"
    };

    private readonly IGatewayResolver _resolver;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IJwtValidator _jwtValidator;
    private readonly IDpopValidator _dpopValidator;
    private readonly IRateLimiterService _rateLimiter;
    private readonly ICatalogCache _cache;
    private readonly ICatalogOverrideApplier _overrideApplier;

    public GatewayProxyHandler(
        IGatewayResolver resolver,
        IHttpClientFactory httpClientFactory,
        IJwtValidator jwtValidator,
        IDpopValidator dpopValidator,
        IRateLimiterService rateLimiter,
        ICatalogCache cache,
        ICatalogOverrideApplier overrideApplier)
    {
        _resolver = resolver;
        _httpClientFactory = httpClientFactory;
        _jwtValidator = jwtValidator;
        _dpopValidator = dpopValidator;
        _rateLimiter = rateLimiter;
        _cache = cache;
        _overrideApplier = overrideApplier;
    }

    public async Task HandleAsync(HttpContext ctx, string tenant, string environment, string configurationName, string? rest, CancellationToken ct)
    {
        var resolved = await _resolver.ResolveAsync(tenant, environment, configurationName, ct);
        var snap = resolved.Snapshot;

        // Auth
        JwtValidationResult? auth = null;
        AuthPolicy? authPolicy = null;

        if (snap.EnforceAuthEnabled)
        {
            if (string.IsNullOrWhiteSpace(snap.AuthPolicyYaml))
                throw new ApiException(500, "Auth enabled but AuthPolicyYaml is missing in deployed snapshot.");

            authPolicy = AuthPolicyParser.Parse(snap.AuthPolicyYaml);
            var bearer = ExtractBearerToken(ctx);
            auth = await _jwtValidator.ValidateAsync(bearer, authPolicy, ct);

            if (authPolicy.Dpop.Enabled)
            {
                _dpopValidator.Validate(ctx, authPolicy, auth.ParsedToken);
            }
        }

        // Rate limiting
        if (snap.RateLimitEnabled)
        {
            if (string.IsNullOrWhiteSpace(snap.RateLimitPolicyYaml))
                throw new ApiException(500, "Rate limit enabled but RateLimitPolicyYaml is missing in deployed snapshot.");

            var rlPolicy = RateLimitPolicyParser.Parse(snap.RateLimitPolicyYaml);
            var identity = ResolveIdentity(ctx, rlPolicy.IdentityKey, auth);
            var decision = await _rateLimiter.CheckAsync(identity, rlPolicy, ct);
            if (!decision.Allowed)
            {
                ctx.Response.StatusCode = decision.StatusCode;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsync($"{{\"error\":\"{EscapeJson(decision.Message)}\"}}", ct);
                return;
            }
        }

        // Determine target endpoint
        rest ??= "";
        var upstreamBase = snap.UpstreamMcpUrl.TrimEnd('/');
        var suffix = rest.StartsWith("/") ? rest : (rest.Length == 0 ? "" : "/" + rest);
        var upstreamUrl = upstreamBase + suffix + ctx.Request.QueryString.Value;

        // Catalog caching / override applies only to tools/resources/prompts endpoints (direct hit)
        var kind = GetCatalogKind(rest);
        var isCatalog = kind is not null;

        if (isCatalog && snap.CatalogCacheTtlMinutes > 0)
        {
            var cacheKey = BuildCacheKey(resolved, kind!.Value, ctx, auth);
            if (_cache.TryGet(cacheKey, out var cached))
            {
                ctx.Response.StatusCode = cached.StatusCode;
                ctx.Response.ContentType = cached.ContentType;
                await ctx.Response.Body.WriteAsync(cached.Body, ct);
                return;
            }
        }

        var http = _httpClientFactory.CreateClient("upstream");

        using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamUrl);

        // copy headers
        foreach (var header in ctx.Request.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;

            if (!upstreamReq.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
            {
                upstreamReq.Content ??= new StreamContent(ctx.Request.Body);
                upstreamReq.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        // body
        if (HasBody(ctx.Request.Method))
        {
            upstreamReq.Content ??= new StreamContent(ctx.Request.Body);
            if (!string.IsNullOrWhiteSpace(ctx.Request.ContentType))
                upstreamReq.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ctx.Request.ContentType);
        }

        using var upstreamRes = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

        ctx.Response.StatusCode = (int)upstreamRes.StatusCode;

        // copy response headers
        foreach (var header in upstreamRes.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            ctx.Response.Headers[header.Key] = header.Value.ToArray();
        }
        foreach (var header in upstreamRes.Content.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key)) continue;
            ctx.Response.Headers[header.Key] = header.Value.ToArray();
        }

        // Kestrel will set its own transfer-encoding
        ctx.Response.Headers.Remove("transfer-encoding");

        var contentType = upstreamRes.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

        if (!isCatalog)
        {
            ctx.Response.ContentType = contentType;
            await upstreamRes.Content.CopyToAsync(ctx.Response.Body, ct);
            return;
        }

        // Catalog response: read, apply override, cache
        var bodyBytes = await upstreamRes.Content.ReadAsByteArrayAsync(ct);
        var outBytes = _overrideApplier.Apply(kind!.Value, bodyBytes, snap.CatalogOverrideYaml);

        ctx.Response.ContentType = contentType;
        await ctx.Response.Body.WriteAsync(outBytes, ct);

        if (snap.CatalogCacheTtlMinutes > 0 && upstreamRes.IsSuccessStatusCode)
        {
            var cacheKey = BuildCacheKey(resolved, kind!.Value, ctx, auth);
            _cache.Set(cacheKey, new CachedResponse((int)upstreamRes.StatusCode, contentType, outBytes),
                TimeSpan.FromMinutes(snap.CatalogCacheTtlMinutes));
        }
    }

    private static bool HasBody(string method)
        => string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)
           || string.Equals(method, "PUT", StringComparison.OrdinalIgnoreCase)
           || string.Equals(method, "PATCH", StringComparison.OrdinalIgnoreCase);

    private static string ExtractBearerToken(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(auth) || !auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ApiException(401, "Missing Authorization: Bearer token.");

        return auth.Substring("Bearer ".Length).Trim();
    }

    private static string ResolveIdentity(HttpContext ctx, string identityKey, JwtValidationResult? auth)
    {
        identityKey = (identityKey ?? "ip").Trim();

        if (string.Equals(identityKey, "subject", StringComparison.OrdinalIgnoreCase))
            return auth?.Subject ?? "anonymous";

        if (string.Equals(identityKey, "client_id", StringComparison.OrdinalIgnoreCase))
            return auth?.ClientId ?? "anonymous";

        if (identityKey.StartsWith("header:", StringComparison.OrdinalIgnoreCase))
        {
            var header = identityKey.Substring("header:".Length).Trim();
            var v = ctx.Request.Headers[header].ToString();
            return string.IsNullOrWhiteSpace(v) ? "anonymous" : v;
        }

        // default IP
        return ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static CatalogKind? GetCatalogKind(string rest)
    {
        rest = (rest ?? "").Trim('/');

        return rest.ToLowerInvariant() switch
        {
            "tools" => CatalogKind.Tools,
            "resources" => CatalogKind.Resources,
            "prompts" => CatalogKind.Prompts,
            _ => null
        };
    }

    private static string BuildCacheKey(ResolvedGateway resolved, CatalogKind kind, HttpContext ctx, JwtValidationResult? auth)
    {
        // Include identity because catalog might differ by auth/rate limit policy needs.
        var identity = auth?.Subject ?? auth?.ClientId ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"catalog:{kind}:{resolved.TenantName}:{resolved.EnvironmentName}:{resolved.ConfigurationName}:{identity}:{ctx.Request.QueryString.Value}";
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
