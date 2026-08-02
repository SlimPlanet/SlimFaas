namespace SlimFaas.RateLimiting;

public class RateLimitingOptions
{
    public const string SectionName = "SlimFaas:RateLimiting";

    public bool Enabled { get; set; }

    public double CpuHighThreshold { get; set; }

    public double CpuLowThreshold { get; set; }

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
