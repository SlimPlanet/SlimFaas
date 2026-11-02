namespace SlimFaas.Security;

/// <summary>
/// Combines the signature result recorded by <see cref="CallerAuthenticationMiddleware"/>
/// with the connection-address rule, according to the configured mode.
/// </summary>
internal static class CallerClassification
{
    /// <summary>
    /// <list type="bullet">
    /// <item>No feature on the request (Legacy mode, or a pipeline without the middleware): the address rule decides.</item>
    /// <item>Verified caller: internal.</item>
    /// <item>Strict: external, unless <paramref name="isPeerAddress"/> (a SlimFaas member pod).</item>
    /// <item>Hybrid: the address rule decides; an unsigned request accepted this way is logged (rate-limited).</item>
    /// </list>
    /// </summary>
    public static bool IsInternal(
        HttpContext context,
        Func<bool> matchesTrustedAddress,
        Func<bool>? isPeerAddress,
        ILogger logger)
    {
        CallerAuthenticationFeature? feature = context.Features.Get<CallerAuthenticationFeature>();
        if (feature is null || feature.Mode == CallerAuthenticationMode.Legacy)
        {
            return matchesTrustedAddress();
        }

        if (feature.Identity is not null)
        {
            return true;
        }

        if (feature.Mode == CallerAuthenticationMode.Strict)
        {
            return isPeerAddress?.Invoke() ?? false;
        }

        if (!matchesTrustedAddress())
        {
            return false;
        }

        LogThrottle? throttle = context.RequestServices?.GetService<LogThrottle>();
        string remote = context.Connection.RemoteIpAddress?.ToString() ?? "";
        string path = context.Request.Path.Value ?? "";
        if (throttle is null || throttle.ShouldLog("unsigned|" + remote + "|" + path))
        {
            logger.LogUnsignedInternalCallerAccepted(remote, path);
        }

        return true;
    }
}
