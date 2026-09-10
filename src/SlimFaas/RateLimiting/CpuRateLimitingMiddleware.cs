using Microsoft.Extensions.Options;

namespace SlimFaas.RateLimiting;

public class CpuRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitingOptions _options;
    private readonly ICpuMetrics _cpuMetrics;
    private readonly int[] _excludedPorts;

    public CpuRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<RateLimitingOptions> options,
        ICpuMetrics cpuMetrics,
        int[] excludedPorts)
    {
        _next = next;
        _options = options.Value;
        _cpuMetrics = cpuMetrics;
        _excludedPorts = excludedPorts;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled || _excludedPorts.Contains(context.Connection.LocalPort))
        {
            await _next(context);
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;
        if (_options.ExcludedPaths.Any(excluded =>
            path.Equals(excluded, StringComparison.OrdinalIgnoreCase) ||
            (path.StartsWith(excluded, StringComparison.OrdinalIgnoreCase) &&
             path.Length > excluded.Length &&
             path[excluded.Length] == '/')))
        {
            await _next(context);
            return;
        }

        if (_cpuMetrics.IsLimiting)
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

            if (_options.RetryAfterSeconds.HasValue)
            {
                context.Response.Headers.RetryAfter = _options.RetryAfterSeconds.Value.ToString();
            }

            await context.Response.WriteAsync("Service temporarily overloaded. Please retry later.");
            return;
        }

        await _next(context);
    }
}
