using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class CpuUsageProviderTests
{
    [Fact]
    public void UpdateCpuUsage_UpdatesCurrentValue()
    {
        var provider = new CpuUsageProvider();

        provider.UpdateCpuUsage(75.5);

        Assert.Equal(75.5, provider.CurrentCpuPercent);
    }

    [Fact]
    public void CurrentCpuPercent_DefaultsToZero()
    {
        var provider = new CpuUsageProvider();

        Assert.Equal(0, provider.CurrentCpuPercent);
    }

    [Fact]
    public async Task GetCurrentCpuUsage_ReturnsNonNegativeValue()
    {
        double cpuUsage = await CpuUsageProvider.GetCurrentCpuUsage();

        Assert.True(cpuUsage >= 0);
        Assert.True(cpuUsage <= 100 * Environment.ProcessorCount);
    }
}
