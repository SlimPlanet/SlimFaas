using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace SlimFaas.RateLimiting;

public class CpuMetrics(IOptions<RateLimitingOptions> options) : ICpuMetrics
{
    private readonly RateLimitingOptions _options = options.Value;
    private double _currentCpuPercent;
    private bool _isLimiting;
    private readonly Lock _lock = new();

    public double CurrentCpuPercent
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _currentCpuPercent;
            }
        }
    }

    public bool IsLimiting
    {
        get
        {
            using (_lock.EnterScope())
            {
                return _isLimiting;
            }
        }
    }

    public void UpdateCpuUsage(double cpuPercent)
    {
        using (_lock.EnterScope())
        {
            _currentCpuPercent = cpuPercent;
            // Evaluate every sample, even when only excluded probes reach the middleware.
            if (!_options.Enabled || cpuPercent <= _options.CpuLowThreshold)
            {
                _isLimiting = false;
            }
            else if (cpuPercent >= _options.CpuHighThreshold)
            {
                _isLimiting = true;
            }
        }
    }

    public static (TimeSpan CpuTime, long TimestampTicks) GetCurrentCpuSnapshot()
    {
        return (Process.GetCurrentProcess().TotalProcessorTime, Stopwatch.GetTimestamp());
    }

    public static double CalculateCpuUsage(
        (TimeSpan CpuTime, long TimestampTicks) start,
        (TimeSpan CpuTime, long TimestampTicks) end)
    {
        double cpuUsedMs = (end.CpuTime - start.CpuTime).TotalMilliseconds;
        double totalMsPassed = (end.TimestampTicks - start.TimestampTicks) * 1000.0 / Stopwatch.Frequency;

        if (totalMsPassed <= 0)
        {
            return 0;
        }

        double cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

        return cpuUsageTotal * 100;
    }
}
