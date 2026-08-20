namespace SlimFaas.RateLimiting;

public class RateLimitingOptions
{
    public const string SectionName = "SlimFaas:RateLimiting";

    public bool Enabled { get; set; } = true;

    public double CpuHighThreshold { get; set; } = 80;

    public double CpuLowThreshold { get; set; } = 60;

    public int SampleIntervalMs { get; set; } = 1000;

    public int? RetryAfterSeconds { get; set; } = 5;

    public string[] ExcludedPaths { get; set; } = ["/health", "/metrics", "/ready", "/SlimData"];

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
