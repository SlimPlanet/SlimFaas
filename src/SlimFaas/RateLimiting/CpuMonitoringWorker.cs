using Microsoft.Extensions.Options;

namespace SlimFaas.RateLimiting;

public class CpuMonitoringWorker : BackgroundService
{
    private readonly ILogger<CpuMonitoringWorker> _logger;
    private readonly RateLimitingOptions _options;
    private readonly CpuMetrics _cpuMetrics;
    private readonly DynamicGaugeService _gauges;

    public CpuMonitoringWorker(
        IOptions<RateLimitingOptions> options,
        CpuMetrics cpuMetrics,
        ILogger<CpuMonitoringWorker> logger,
        DynamicGaugeService gauges)
    {
        _logger = logger;
        _options = options.Value;
        _cpuMetrics = cpuMetrics;
        _gauges = gauges;
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
                RecordCpuUsage(cpuPercent);
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

    internal void RecordCpuUsage(double cpuPercent)
    {
        bool wasLimiting = _cpuMetrics.IsLimiting;
        _cpuMetrics.UpdateCpuUsage(cpuPercent);
        bool isLimiting = _cpuMetrics.IsLimiting;

        _gauges.SetGaugeValue("slimfaas_cpu_usage_percent", cpuPercent,
            "Process CPU usage as a percentage of the processors available to the runtime");
        _gauges.SetGaugeValue("slimfaas_cpu_rate_limiting_active", isLimiting ? 1 : 0,
            "Whether CPU rate limiting is currently rejecting non-exempt requests");

        if (isLimiting != wasLimiting)
        {
            if (isLimiting)
            {
                _logger.LogWarning(
                    "CPU rate limiting activated. CPU: {CpuPercent:F2}%, Threshold: {Threshold}%",
                    cpuPercent, _options.CpuHighThreshold);
            }
            else
            {
                _logger.LogInformation(
                    "CPU rate limiting deactivated. CPU: {CpuPercent:F2}%, Threshold: {Threshold}%",
                    cpuPercent, _options.CpuLowThreshold);
            }
        }

        if (_logger.IsEnabled(LogLevel.Warning) && cpuPercent >= _options.CpuHighThreshold)
        {
            _logger.LogWarning("High CPU usage detected: {CpuPercent:F2}%", cpuPercent);
        }
    }
}
