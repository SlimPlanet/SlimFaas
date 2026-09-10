namespace SlimFaas.RateLimiting;

public interface ICpuMetrics
{
    double CurrentCpuPercent { get; }
    bool IsLimiting { get; }
}
