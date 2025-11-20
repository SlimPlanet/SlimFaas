namespace SlimFaas.Kubernetes;

public interface IMetricsScrapingGuard
{
    bool IsEnabled { get; }
    void EnablePromql();
}

public sealed class MetricsScrapingGuard : IMetricsScrapingGuard
{
    private int _enabled; // 0/1

    public bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    public void EnablePromql() => Interlocked.Exchange(ref _enabled, 1);
}
