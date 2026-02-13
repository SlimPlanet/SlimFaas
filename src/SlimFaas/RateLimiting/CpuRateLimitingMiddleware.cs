using Microsoft.Extensions.Options;

namespace SlimFaas.RateLimiting;

public class CpuRateLimitingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly RateLimitingOptions _options;
    private readonly ICpuMetrics _cpuMetrics;
    private readonly ILogger<CpuRateLimitingMiddleware> _logger;
    private readonly int[] _excludedPorts;
    private bool _isLimiting;

    public CpuRateLimitingMiddleware(
        RequestDelegate next,
        IOptions<RateLimitingOptions> options,
        ICpuMetrics cpuMetrics,
        ILogger<CpuRateLimitingMiddleware> logger,
        int[] excludedPorts)
    {
        _next = next;
        _options = options.Value;
        _cpuMetrics = cpuMetrics;
        _logger = logger;
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
            path.Equals(excluded, StringComparison.OrdinalIgnoreCase)))
        {
            await _next(context);
            return;
        }

        double currentCpu = _cpuMetrics.CurrentCpuPercent;

        if (currentCpu >= _options.CpuHighThreshold)
        {
            StartCpuRateLimiting(currentCpu);
        }
        else if (currentCpu <= _options.CpuLowThreshold)
        {
            StopCpuRateLimiting(currentCpu);
        }

        if (_isLimiting)
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

    private void StartCpuRateLimiting(double currentCpu)
    {
        if (_isLimiting)
        {
            return;
        }

        _isLimiting = true;
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "CPU rate limiting activated. CPU: {CpuPercent:F2}%, Threshold: {Threshold}%",
                currentCpu,
                _options.CpuHighThreshold);
        }
    }

    private void StopCpuRateLimiting(double currentCpu)
    {
        if (!_isLimiting)
        {
            return;
        }

        _isLimiting = false;
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "CPU rate limiting deactivated. CPU: {CpuPercent:F2}%, Threshold: {Threshold}%",
                currentCpu,
                _options.CpuLowThreshold);
        }
    }
}
