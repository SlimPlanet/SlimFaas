using Microsoft.Extensions.Options;

namespace SlimFaas.RateLimiting;

public class CpuMonitoringService : BackgroundService
{
    private readonly ILogger<CpuMonitoringService> _logger;
    private readonly RateLimitingOptions _options;
    private readonly CpuUsageProvider _cpuUsageProvider;

    public CpuMonitoringService(
        IOptions<RateLimitingOptions> options,
        CpuUsageProvider cpuUsageProvider,
        ILogger<CpuMonitoringService> logger)
    {
        _logger = logger;
        _options = options.Value;
        _cpuUsageProvider = cpuUsageProvider;
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

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                double cpuPercent = CpuUsageProvider.GetCurrentCpuUsage();
                _cpuUsageProvider.UpdateCpuUsage(cpuPercent);

                if (_logger.IsEnabled(LogLevel.Warning) && cpuPercent >= _options.CpuHighThreshold)
                {
                    _logger.LogWarning("High CPU usage detected: {CpuPercent:F2}%", cpuPercent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring CPU usage");
            }

            await Task.Delay(_options.SampleIntervalMs, stoppingToken);
        }
    }
}
