using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Auth;

public enum RateLimiterType
{
    FixedWindow,
    SlidingWindow,
    TokenBucket,
    Concurrency
}

public sealed class RateLimitPolicy
{
    public RateLimiterType Type { get; init; } = RateLimiterType.FixedWindow;
    public int PermitLimit { get; init; } = 10;
    public int WindowSeconds { get; init; } = 60;
    public int QueueLimit { get; init; } = 0;
    public string IdentityKey { get; init; } = "ip"; // subject|client_id|ip|header:X-Api-Key
    public int RejectionStatusCode { get; init; } = 429;
    public string RejectionMessage { get; init; } = "Too Many Requests";
}

public static class RateLimitPolicyParser
{
    public static RateLimitPolicy Parse(string yaml)
    {
        try
        {
            var root = SimpleYaml.AsMapping(SimpleYaml.Parse(yaml));
            var typeStr = (SimpleYaml.TryGetString(root, "type") ?? "fixedWindow").Trim();

            var type = typeStr.ToLowerInvariant() switch
            {
                "fixedwindow" or "fixed_window" or "fixed" => RateLimiterType.FixedWindow,
                "slidingwindow" or "sliding_window" or "sliding" => RateLimiterType.SlidingWindow,
                "tokenbucket" or "token_bucket" or "token" => RateLimiterType.TokenBucket,
                "concurrency" => RateLimiterType.Concurrency,
                _ => throw new ApiException(400, "Rate limit policy YAML: invalid 'type'.")
            };

            var permit = SimpleYaml.TryGetInt(root, "permitLimit") ?? 10;
            var windowSeconds = SimpleYaml.TryGetInt(root, "windowSeconds") ?? 60;
            var queue = SimpleYaml.TryGetInt(root, "queueLimit") ?? 0;
            var identity = (SimpleYaml.TryGetString(root, "identity") ?? "ip").Trim();
            var rejCode = SimpleYaml.TryGetInt(root, "rejectionStatusCode") ?? 429;
            var rejMsg = (SimpleYaml.TryGetString(root, "rejectionMessage") ?? "Too Many Requests").Trim();

            if (permit <= 0) throw new ApiException(400, "Rate limit policy YAML: permitLimit must be > 0.");
            if (windowSeconds <= 0) throw new ApiException(400, "Rate limit policy YAML: windowSeconds must be > 0.");
            if (queue < 0) throw new ApiException(400, "Rate limit policy YAML: queueLimit must be >= 0.");

            if (identity.StartsWith("header:", StringComparison.OrdinalIgnoreCase))
            {
                var header = identity.Substring("header:".Length).Trim();
                if (string.IsNullOrWhiteSpace(header)) throw new ApiException(400, "Rate limit policy YAML: header identity requires a header name.");
            }

            return new RateLimitPolicy
            {
                Type = type,
                PermitLimit = permit,
                WindowSeconds = windowSeconds,
                QueueLimit = queue,
                IdentityKey = identity,
                RejectionStatusCode = rejCode,
                RejectionMessage = rejMsg
            };
        }
        catch (ApiException) { throw; }
        catch (Exception ex)
        {
            throw new ApiException(400, $"Rate limit policy YAML is invalid: {ex.Message}");
        }
    }
}
