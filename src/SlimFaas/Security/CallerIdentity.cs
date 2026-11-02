namespace SlimFaas.Security;

/// <summary>
/// The caller proven by a valid request signature. Available through
/// <see cref="CallerIdentityHttpContextExtensions.GetCallerIdentity"/> for later
/// authorization work (per-function allow-lists, issue #411).
/// </summary>
/// <param name="CallerId">The <c>caller-id</c> whose key signed the request.</param>
public sealed record CallerIdentity(string CallerId);

/// <summary>
/// Per-request result of the caller-authentication middleware. Absent when the mode is
/// <see cref="CallerAuthenticationMode.Legacy"/> (the middleware is not in the pipeline),
/// in which case every classification falls back to the address rule.
/// </summary>
internal sealed class CallerAuthenticationFeature(CallerAuthenticationMode mode, CallerIdentity? identity)
{
    public CallerAuthenticationMode Mode { get; } = mode;

    public CallerIdentity? Identity { get; } = identity;
}

public static class CallerIdentityHttpContextExtensions
{
    /// <summary>The verified caller of the request, or <c>null</c> when the request is not signed.</summary>
    public static CallerIdentity? GetCallerIdentity(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<CallerAuthenticationFeature>()?.Identity;
    }
}
