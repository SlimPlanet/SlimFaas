using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class CpuMetricsTests
{
    [Fact]
    public void UpdateCpuUsage_UpdatesCurrentValue()
    {
        var cpuMetrics = new CpuMetrics();

        cpuMetrics.UpdateCpuUsage(75.5);

        Assert.Equal(75.5, cpuMetrics.CurrentCpuPercent);
    }

    [Fact]
    public void CurrentCpuPercent_DefaultsToZero()
    {
        var cpuMetrics = new CpuMetrics();

        Assert.Equal(0, cpuMetrics.CurrentCpuPercent);
    }

    [Fact]
    public void GetCurrentCpuSnapshot_ReturnsValidSnapshot()
    {
        var snapshot = CpuMetrics.GetCurrentCpuSnapshot();

        Assert.True(snapshot.CpuTime.TotalMilliseconds >= 0);
        Assert.True(snapshot.TimestampTicks > 0);
    }

    [Fact]
    public async Task CalculateCpuUsage_WithTimePassed_ReturnsValidPercentage()
    {
        var start = CpuMetrics.GetCurrentCpuSnapshot();

        await Task.Delay(100);

        var end = CpuMetrics.GetCurrentCpuSnapshot();
        double cpuUsage = CpuMetrics.CalculateCpuUsage(start, end);

        Assert.True(cpuUsage >= 0);
        Assert.True(cpuUsage <= 100 * Environment.ProcessorCount);
    }

    [Fact]
    public void CalculateCpuUsage_WithSameTimestamp_ReturnsZero()
    {
        var snapshot = CpuMetrics.GetCurrentCpuSnapshot();

        double cpuUsage = CpuMetrics.CalculateCpuUsage(snapshot, snapshot);

        Assert.Equal(0, cpuUsage);
    }

    [Fact]
    public void CalculateCpuUsage_WithReverseTime_ReturnsZero()
    {
        var start = CpuMetrics.GetCurrentCpuSnapshot();
        var end = (start.CpuTime, start.TimestampTicks - 1000);

        double cpuUsage = CpuMetrics.CalculateCpuUsage(start, end);

        Assert.Equal(0, cpuUsage);
    }
}
