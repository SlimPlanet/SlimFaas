using System.Diagnostics;

namespace SlimFaas.RateLimiting;

public class CpuUsageProvider : ICpuUsageProvider
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

    public static async Task<double> GetCurrentCpuUsage()
    {
        var startTime = DateTime.UtcNow;
        var startCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        await timer.WaitForNextTickAsync();

        var endTime = DateTime.UtcNow;
        var endCpuUsage = Process.GetCurrentProcess().TotalProcessorTime;

        double cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
        double totalMsPassed = (endTime - startTime).TotalMilliseconds;
        double cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);

        return cpuUsageTotal * 100;
    }
}
