using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using SlimFaasMcpGateway.Auth;

namespace SlimFaasMcpGateway.Gateway;

public sealed record RateLimitDecision(bool Allowed, int StatusCode, string Message);

public interface IRateLimiterService
{
    ValueTask<RateLimitDecision> CheckAsync(string identity, RateLimitPolicy policy, CancellationToken ct);
}

public sealed class RateLimiterService : IRateLimiterService
{
    private readonly ConcurrentDictionary<string, RateLimiter> _limiters = new(StringComparer.Ordinal);

    public async ValueTask<RateLimitDecision> CheckAsync(string identity, RateLimitPolicy policy, CancellationToken ct)
    {
        identity = string.IsNullOrWhiteSpace(identity) ? "anonymous" : identity;

        var key = $"{policy.Type}:{policy.PermitLimit}:{policy.WindowSeconds}:{policy.QueueLimit}:{identity}";
        var limiter = _limiters.GetOrAdd(key, _ => Create(policy));

        RateLimitLease lease;
        try
        {
            lease = await limiter.AcquireAsync(1, ct);
        }
        catch (OperationCanceledException) { throw; }

        if (lease.IsAcquired)
            return new RateLimitDecision(true, 0, "");

        return new RateLimitDecision(false, policy.RejectionStatusCode, policy.RejectionMessage);
    }

    private static RateLimiter Create(RateLimitPolicy policy)
    {
        return policy.Type switch
        {
            RateLimiterType.FixedWindow => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = TimeSpan.FromSeconds(policy.WindowSeconds),
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }),
            RateLimiterType.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                Window = TimeSpan.FromSeconds(policy.WindowSeconds),
                SegmentsPerWindow = 4,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }),
            RateLimiterType.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = policy.PermitLimit,
                TokensPerPeriod = policy.PermitLimit,
                ReplenishmentPeriod = TimeSpan.FromSeconds(policy.WindowSeconds),
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            }),
            RateLimiterType.Concurrency => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = policy.PermitLimit,
                QueueLimit = policy.QueueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }),
            _ => throw new InvalidOperationException("Unknown limiter type")
        };
    }
}
