using System.ComponentModel.DataAnnotations;

namespace SlimFaas.RateLimiting;

public class RateLimitingOptions
{
    public const string SectionName = "SlimFaas:RateLimiting";

    public bool Enabled { get; set; }

    [Range(0, 100)]
    public double CpuHighThreshold { get; set; }

    [Range(0, 100)]
    public double CpuLowThreshold { get; set; }

    [Range(100, int.MaxValue)]
    public int SampleIntervalMs { get; set; }

    public int? RetryAfterSeconds { get; set; }

    public string[] ExcludedPaths { get; set; } = [];

    public bool IsValid()
    {
        if (CpuLowThreshold is < 0 or > 100)
        {
            return false;
        }

        if (CpuHighThreshold is < 0 or > 100)
        {
            return false;
        }

        if (CpuLowThreshold >= CpuHighThreshold)
        {
            return false;
        }

        return SampleIntervalMs >= 100;
    }
}
