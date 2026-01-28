
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Auth;

public interface IDpopValidator
{
    void Validate(HttpContext ctx, AuthPolicy policy, JwtSecurityToken accessToken);
}

public sealed class DpopValidator : IDpopValidator
{
    public void Validate(HttpContext ctx, AuthPolicy policy, JwtSecurityToken accessToken)
    {
        if (!policy.Dpop.Enabled)
            return;

        if (!ctx.Request.Headers.TryGetValue("DPoP", out var dpopHeader) || string.IsNullOrWhiteSpace(dpopHeader))
            throw new ApiException(401, "Missing DPoP proof.");

        var proof = dpopHeader.ToString();
        var handler = new JwtSecurityTokenHandler();

        JwtSecurityToken dpopJwt;
        try
        {
            dpopJwt = handler.ReadJwtToken(proof);
        }
        catch (Exception ex)
        {
            throw new ApiException(401, $"Invalid DPoP proof: {ex.Message}");
        }

        // Get JWK from header
        if (!dpopJwt.Header.TryGetValue("jwk", out var jwkObj) || jwkObj is null)
            throw new ApiException(401, "DPoP proof missing 'jwk' header.");

        var jwkJson = JsonSerializer.Serialize(jwkObj);
        var jwk = new JsonWebKey(jwkJson);

        // Validate signature (issuer/audience/lifetime are not used for DPoP proof)
        try
        {
            _ = handler.ValidateToken(proof, new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = jwk
            }, out _);
        }
        catch (SecurityTokenException ex)
        {
            throw new ApiException(401, $"Invalid DPoP signature: {ex.Message}");
        }

        var htm = dpopJwt.Claims.FirstOrDefault(c => c.Type == "htm")?.Value;
        var htu = dpopJwt.Claims.FirstOrDefault(c => c.Type == "htu")?.Value;
        var iatStr = dpopJwt.Claims.FirstOrDefault(c => c.Type == "iat")?.Value;
        var nonce = dpopJwt.Claims.FirstOrDefault(c => c.Type == "nonce")?.Value;

        if (string.IsNullOrWhiteSpace(htm) || string.IsNullOrWhiteSpace(htu) || string.IsNullOrWhiteSpace(iatStr))
            throw new ApiException(401, "DPoP proof missing required claims (htm, htu, iat).");

        if (!string.Equals(htm, ctx.Request.Method, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(401, "DPoP 'htm' does not match request method.");

        var expectedHtu = BuildAbsoluteRequestUri(ctx);
        if (!UriEqualsLoose(htu, expectedHtu))
            throw new ApiException(401, "DPoP 'htu' does not match request URL.");

        if (!long.TryParse(iatStr, out var iat))
            throw new ApiException(401, "DPoP 'iat' is invalid.");

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var window = Math.Max(1, policy.Dpop.IatWindowSeconds);
        if (iat > now + 60 || now - iat > window)
            throw new ApiException(401, "DPoP proof is outside allowed iat window.");

        if (policy.Dpop.RequireNonce && string.IsNullOrWhiteSpace(nonce))
            throw new ApiException(401, "DPoP nonce is required by policy.");

        // Validate binding to access token via cnf.jkt
        var cnf = accessToken.Claims.FirstOrDefault(c => c.Type == "cnf")?.Value;
        if (string.IsNullOrWhiteSpace(cnf))
            throw new ApiException(401, "Access token missing cnf claim required for DPoP binding.");

        var jkt = ExtractJkt(cnf);
        if (string.IsNullOrWhiteSpace(jkt))
            throw new ApiException(401, "Access token cnf claim missing jkt.");

        var proofJkt = ComputeJwkThumbprint(jwk);
        if (!string.Equals(jkt, proofJkt, StringComparison.Ordinal))
            throw new ApiException(401, "DPoP proof key is not bound to access token (cnf.jkt mismatch).");
    }

    private static string BuildAbsoluteRequestUri(HttpContext ctx)
    {
        var req = ctx.Request;
        var scheme = req.Scheme;
        var host = req.Host.ToUriComponent();
        var path = req.Path.ToUriComponent();
        var query = req.QueryString.HasValue ? req.QueryString.Value : "";
        return $"{scheme}://{host}{path}{query}";
    }

    private static bool UriEqualsLoose(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var ua)) return false;
        if (!Uri.TryCreate(b, UriKind.Absolute, out var ub)) return false;

        // Compare scheme, host, path, query (case-insensitive host)
        if (!string.Equals(ua.Scheme, ub.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(ua.Host, ub.Host, StringComparison.OrdinalIgnoreCase)) return false;

        var ap = ua.AbsolutePath.TrimEnd('/');
        var bp = ub.AbsolutePath.TrimEnd('/');
        if (!string.Equals(ap, bp, StringComparison.Ordinal)) return false;

        // Query must match exactly
        if (!string.Equals(ua.Query ?? "", ub.Query ?? "", StringComparison.Ordinal)) return false;

        // Ports: treat default ports as equal
        var pa = ua.IsDefaultPort ? DefaultPort(ua.Scheme) : ua.Port;
        var pb = ub.IsDefaultPort ? DefaultPort(ub.Scheme) : ub.Port;
        return pa == pb;
    }

    private static int DefaultPort(string scheme) => scheme.ToLowerInvariant() switch
    {
        "https" => 443,
        "http" => 80,
        _ => -1
    };

    private static string? ExtractJkt(string cnfClaimValue)
    {
        try
        {
            using var doc = JsonDocument.Parse(cnfClaimValue);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("jkt", out var jktProp) &&
                jktProp.ValueKind == JsonValueKind.String)
                return jktProp.GetString();
        }
        catch
        {
            // Some providers put cnf as a nested JSON string in a claim - try to unescape
            try
            {
                var unescaped = cnfClaimValue.Trim().Trim('\"');
                using var doc2 = JsonDocument.Parse(unescaped);
                if (doc2.RootElement.ValueKind == JsonValueKind.Object &&
                    doc2.RootElement.TryGetProperty("jkt", out var jktProp2) &&
                    jktProp2.ValueKind == JsonValueKind.String)
                    return jktProp2.GetString();
            }
            catch { }
        }

        return null;
    }

    // RFC 7638 thumbprint (SHA-256) for RSA / EC keys
    private static string ComputeJwkThumbprint(JsonWebKey jwk)
    {
        string json;
        if (string.Equals(jwk.Kty, "RSA", StringComparison.OrdinalIgnoreCase))
        {
            json = $"{{\"e\":\"{jwk.E}\",\"kty\":\"RSA\",\"n\":\"{jwk.N}\"}}";
        }
        else if (string.Equals(jwk.Kty, "EC", StringComparison.OrdinalIgnoreCase))
        {
            json = $"{{\"crv\":\"{jwk.Crv}\",\"kty\":\"EC\",\"x\":\"{jwk.X}\",\"y\":\"{jwk.Y}\"}}";
        }
        else
        {
            throw new ApiException(401, $"Unsupported DPoP JWK kty '{jwk.Kty}'.");
        }

        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncoder.Encode(hash);
    }
}
