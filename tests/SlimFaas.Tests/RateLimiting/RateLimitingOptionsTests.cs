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
            CpuHighThreshold = 60,
            CpuLowThreshold = 80,
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
            CpuHighThreshold = 80,
            CpuLowThreshold = 60,
            SampleIntervalMs = 50
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_WithCpuHighThresholdGreaterThan100_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            CpuHighThreshold = 120,
            CpuLowThreshold = 60,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_WithCpuLowThresholdLessThan0_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            CpuHighThreshold = 80,
            CpuLowThreshold = -10,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_WithCpuHighThresholdLessThan0_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            CpuHighThreshold = -5,
            CpuLowThreshold = 10,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }

    [Fact]
    public void IsValid_WithCpuLowThresholdGreaterThan100_ReturnsFalse()
    {
        var options = new RateLimitingOptions
        {
            CpuHighThreshold = 90,
            CpuLowThreshold = 120,
            SampleIntervalMs = 1000
        };

        bool isValid = options.IsValid();

        Assert.False(isValid);
    }
}
