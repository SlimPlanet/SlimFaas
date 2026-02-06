using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class RateLimitingOptionsTests
{
    [Fact]
    public void IsValid_WithValidOptions_ReturnsTrue()
    {
        var options = new RateLimitingOptions
        {
            Enabled = true,
            PublicPort = 5000,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.True(isValid);
    }

    [Fact]
    public void IsValid_WithLowThresholdGreaterThanHigh_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            PublicPort = 5000,
            CpuHighThreshold = 60,
            CpuLowThreshold = 80,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_WithInvalidPort_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            PublicPort = 0,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_WithLowSampleInterval_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            PublicPort = 5000,
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            SampleIntervalMs = 50
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }
}
