using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class CpuMetricsTests
{
    [Fact]
    public void UpdateCpuUsage_UpdatesCurrentValue()
    {
        var cpuMetrics = new CpuMetrics(Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions()));

        cpuMetrics.UpdateCpuUsage(75.5);

        Assert.Equal(75.5, cpuMetrics.CurrentCpuPercent);
    }

    [Fact]
    public void CurrentCpuPercent_DefaultsToZero()
    {
        var cpuMetrics = new CpuMetrics(Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions()));

        Assert.Equal(0, cpuMetrics.CurrentCpuPercent);
        Assert.False(cpuMetrics.IsLimiting);
    }

    [Theory]
    [InlineData(false, 59.9, false)]
    [InlineData(false, 60, false)]
    [InlineData(false, 70, false)]
    [InlineData(false, 79.9, false)]
    [InlineData(false, 80, true)]
    [InlineData(false, 85, true)]
    [InlineData(true, 59.9, false)]
    [InlineData(true, 60, false)]
    [InlineData(true, 60.1, true)]
    [InlineData(true, 70, true)]
    [InlineData(true, 80, true)]
    public void UpdateCpuUsage_AppliesInclusiveThresholdsAndHysteresis(bool initiallyLimiting, double sample, bool expected)
    {
        var metrics = new CpuMetrics(Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions()));
        if (initiallyLimiting)
            metrics.UpdateCpuUsage(85);

        metrics.UpdateCpuUsage(sample);

        Assert.Equal(sample, metrics.CurrentCpuPercent);
        Assert.Equal(expected, metrics.IsLimiting);
    }

    [Fact]
    public void UpdateCpuUsage_UsesConfiguredThresholds()
    {
        var metrics = new CpuMetrics(Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions
        {
            CpuHighThreshold = 40,
            CpuLowThreshold = 20
        }));

        metrics.UpdateCpuUsage(40);
        Assert.True(metrics.IsLimiting);
        metrics.UpdateCpuUsage(30);
        Assert.True(metrics.IsLimiting);
        metrics.UpdateCpuUsage(20);
        Assert.False(metrics.IsLimiting);
        metrics.UpdateCpuUsage(30);
        Assert.False(metrics.IsLimiting);
    }

    [Fact]
    public void UpdateCpuUsage_WhenDisabled_NeverLimits()
    {
        var metrics = new CpuMetrics(Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions { Enabled = false }));

        metrics.UpdateCpuUsage(100);

        Assert.Equal(100, metrics.CurrentCpuPercent);
        Assert.False(metrics.IsLimiting);
    }

    [Fact]
    public void GetCurrentCpuSnapshot_ReturnsValidSnapshot()
    {
        var snapshot = CpuMetrics.GetCurrentCpuSnapshot();

        Assert.True(snapshot.CpuTime.TotalMilliseconds >= 0);
        Assert.True(snapshot.TimestampTicks > 0);
    }

    [Fact]
    public void CalculateCpuUsage_WithTimePassed_ReturnsValidPercentage()
    {
        var start = (TimeSpan.Zero, 0L);
        var end = (TimeSpan.FromMilliseconds(750 * Environment.ProcessorCount), System.Diagnostics.Stopwatch.Frequency);
        double cpuUsage = CpuMetrics.CalculateCpuUsage(start, end);

        Assert.Equal(75, cpuUsage);
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
