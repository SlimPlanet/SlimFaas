using System.Diagnostics;

namespace SlimFaas.RateLimiting;

public class CpuMetrics : ICpuMetrics
{
    private double _currentCpuPercent;
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

    public void UpdateCpuUsage(double cpuPercent)
    {
        using (_lock.EnterScope())
        {
            _currentCpuPercent = cpuPercent;
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
