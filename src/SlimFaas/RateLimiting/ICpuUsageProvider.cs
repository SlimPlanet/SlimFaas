namespace SlimFaas.RateLimiting;

public interface ICpuUsageProvider
{
    double CurrentCpuPercent { get; }
}
