
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Auth;

public sealed record JwtValidationResult(ClaimsPrincipal Principal, string? Subject, string? ClientId, JwtSecurityToken ParsedToken);

public interface IJwtValidator
{
    Task<JwtValidationResult> ValidateAsync(string bearerToken, AuthPolicy policy, CancellationToken ct);
}

public sealed class JwtValidator : IJwtValidator
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    public JwtValidator(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    public async Task<JwtValidationResult> ValidateAsync(string bearerToken, AuthPolicy policy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ApiException(401, "Missing bearer token.");

        var handler = new JwtSecurityTokenHandler();

        var keys = await GetSigningKeysAsync(policy.JwksUrl!, ct);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = policy.Issuers,
            ValidateAudience = true,
            ValidAudiences = policy.Audiences,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            RequireSignedTokens = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(Math.Max(0, policy.ClockSkewSeconds)),
            ValidAlgorithms = policy.Algorithms
        };

        try
        {
            var principal = handler.ValidateToken(bearerToken, parameters, out var validatedToken);
            var jwt = validatedToken as JwtSecurityToken ?? handler.ReadJwtToken(bearerToken);

            EnforceRequiredClaims(principal, policy.RequiredClaims);

            var sub = principal.FindFirstValue("sub");
            var clientId = principal.FindFirstValue("client_id") ?? principal.FindFirstValue("azp");

            return new JwtValidationResult(principal, sub, clientId, jwt);
        }
        catch (SecurityTokenException ex)
        {
            throw new ApiException(401, $"Invalid token: {ex.Message}");
        }
    }

    private void EnforceRequiredClaims(ClaimsPrincipal principal, Dictionary<string, string> required)
    {
        foreach (var kv in required)
        {
            var claim = principal.FindFirst(kv.Key);
            if (claim is null)
                throw new ApiException(403, $"Missing required claim '{kv.Key}'.");

            var expected = kv.Value;
            if (string.IsNullOrWhiteSpace(expected) || expected == "*")
                continue;

            if (string.Equals(kv.Key, "scope", StringComparison.OrdinalIgnoreCase))
            {
                var scopes = (claim.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!scopes.Contains(expected, StringComparer.OrdinalIgnoreCase))
                    throw new ApiException(403, $"Required scope '{expected}' not present.");
            }
            else
            {
                if (!string.Equals(claim.Value, expected, StringComparison.OrdinalIgnoreCase))
                    throw new ApiException(403, $"Claim '{kv.Key}' must equal '{expected}'.");
            }
        }
    }

    private async Task<IReadOnlyCollection<SecurityKey>> GetSigningKeysAsync(string jwksUrl, CancellationToken ct)
    {
        var cacheKey = "jwks:" + jwksUrl;
        if (_cache.TryGetValue(cacheKey, out IReadOnlyCollection<SecurityKey>? cached) && cached is not null)
            return cached;

        var http = _httpClientFactory.CreateClient("upstream");
        var json = await http.GetStringAsync(jwksUrl, ct);

        JsonWebKeySet jwks;
        try
        {
            jwks = new JsonWebKeySet(json);
        }
        catch (Exception ex)
        {
            throw new ApiException(502, $"Failed to parse JWKS: {ex.Message}");
        }

        var keys = jwks.Keys.Cast<SecurityKey>().ToList();

        // Cache for 10 minutes
        _cache.Set(cacheKey, keys, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) });

        return keys;
    }
}
