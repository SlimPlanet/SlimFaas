using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Auth;

public sealed class AuthPolicy
{
    public List<string> Issuers { get; init; } = new();
    public List<string> Audiences { get; init; } = new();
    public string? JwksUrl { get; init; }
    public List<string> Algorithms { get; init; } = new() { "RS256" };
    public Dictionary<string, string> RequiredClaims { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int ClockSkewSeconds { get; init; } = 60;
    public DpopPolicy Dpop { get; init; } = new();
}

public sealed class DpopPolicy
{
    public bool Enabled { get; init; } = false;
    public int IatWindowSeconds { get; init; } = 300;
    public bool RequireNonce { get; init; } = false;
}

public static class AuthPolicyParser
{
    public static AuthPolicy Parse(string yaml)
    {
        try
        {
            var root = SimpleYaml.AsMapping(SimpleYaml.Parse(yaml));

            var issuers = ReadStringList(root, "issuer");
            var audiences = ReadStringList(root, "audience");
            var jwksUrl = SimpleYaml.TryGetString(root, "jwksUrl") ?? SimpleYaml.TryGetString(root, "jwks_url");

            var algos = ReadStringList(root, "algorithms");
            if (algos.Count == 0) algos.Add("RS256");

            var clockSkew = SimpleYaml.TryGetInt(root, "clockSkewSeconds") ?? 60;

            var requiredClaims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (SimpleYaml.TryGet(root, "requiredClaims") is SimpleYaml.Mapping rcMap)
            {
                foreach (var kv in rcMap.Values)
                {
                    if (kv.Value is SimpleYaml.Scalar s && s.Value is not null)
                        requiredClaims[kv.Key] = s.Value.ToString()!;
                }
            }

            var dpop = new DpopPolicy();
            if (SimpleYaml.TryGet(root, "dpop") is SimpleYaml.Mapping dpopMap)
            {
                dpop = new DpopPolicy
                {
                    Enabled = SimpleYaml.TryGetBool(dpopMap, "enabled") ?? false,
                    IatWindowSeconds = SimpleYaml.TryGetInt(dpopMap, "iatWindowSeconds") ?? 300,
                    RequireNonce = SimpleYaml.TryGetBool(dpopMap, "requireNonce") ?? false
                };
            }

            if (issuers.Count == 0)
                throw new ApiException(400, "Auth policy YAML: 'issuer' is required (list).");

            if (audiences.Count == 0)
                throw new ApiException(400, "Auth policy YAML: 'audience' is required (list).");

            if (string.IsNullOrWhiteSpace(jwksUrl))
                throw new ApiException(400, "Auth policy YAML: 'jwksUrl' is required.");

            InputValidators.ValidateAbsoluteHttpUrl(jwksUrl!, "Auth policy jwksUrl");

            return new AuthPolicy
            {
                Issuers = issuers,
                Audiences = audiences,
                JwksUrl = jwksUrl,
                Algorithms = algos,
                RequiredClaims = requiredClaims,
                ClockSkewSeconds = clockSkew,
                Dpop = dpop
            };
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            throw new ApiException(400, $"Auth policy YAML is invalid: {ex.Message}");
        }
    }

    private static List<string> ReadStringList(SimpleYaml.Mapping root, string key)
    {
        var list = new List<string>();
        var node = SimpleYaml.TryGet(root, key);
        if (node is SimpleYaml.Sequence seq)
        {
            foreach (var it in seq.Items)
            {
                if (it is SimpleYaml.Scalar s && s.Value is not null)
                    list.Add(s.Value.ToString()!.Trim());
            }
        }
        return list.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
