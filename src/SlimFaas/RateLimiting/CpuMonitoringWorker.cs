using Microsoft.Extensions.Options;

namespace SlimFaas.RateLimiting;

public class CpuMonitoringWorker : BackgroundService
{
    private readonly ILogger<CpuMonitoringWorker> _logger;
    private readonly RateLimitingOptions _options;
    private readonly CpuMetrics _cpuMetrics;

    public CpuMonitoringWorker(
        IOptions<RateLimitingOptions> options,
        CpuMetrics cpuMetrics,
        ILogger<CpuMonitoringWorker> logger)
    {
        _logger = logger;
        _options = options.Value;
        _cpuMetrics = cpuMetrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("CPU monitoring is disabled");
            return;
        }

        _logger.LogInformation(
            "CPU monitoring started. Interval: {IntervalMs}ms, High: {High}%, Low: {Low}%",
            _options.SampleIntervalMs,
            _options.CpuHighThreshold,
            _options.CpuLowThreshold);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.SampleIntervalMs));

        await timer.WaitForNextTickAsync(stoppingToken);

        var previousSnapshot = CpuMetrics.GetCurrentCpuSnapshot();

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var currentSnapshot = CpuMetrics.GetCurrentCpuSnapshot();

            try
            {
                double cpuPercent = CpuMetrics.CalculateCpuUsage(previousSnapshot, currentSnapshot);
                _cpuMetrics.UpdateCpuUsage(cpuPercent);

                if (_logger.IsEnabled(LogLevel.Warning) && cpuPercent >= _options.CpuHighThreshold)
                {
                    _logger.LogWarning("High CPU usage detected: {CpuPercent:F2}%", cpuPercent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring CPU usage");
            }
            finally
            {
                previousSnapshot = currentSnapshot;
            }
        }
    }
}
