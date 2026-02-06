using System.ComponentModel.DataAnnotations;

namespace SlimFaas.RateLimiting;

public class RateLimitingOptions
{
    public const string SectionName = "SlimFaas:RateLimiting";

    public bool Enabled { get; set; }

    [Range(1, 65535)]
    public int PublicPort { get; set; }

    [Range(0, 100)]
    public double CpuHighThreshold { get; set; }

    [Range(0, 100)]
    public double CpuLowThreshold { get; set; }

    [Range(100, int.MaxValue)]
    public int SampleIntervalMs { get; set; }

    [Range(100, 599)]
    public int StatusCode { get; set; } = 429;

    public int? RetryAfterSeconds { get; set; }

    public string[] ExcludedPaths { get; set; } = [];

    public bool IsValid()
    {
        if (PublicPort <= 0)
        {
            return false;
        }

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
