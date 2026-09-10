using System.Text;
using Microsoft.Extensions.Logging;
using Prometheus;
using SlimFaas.RateLimiting;

namespace SlimFaas.Tests.RateLimiting;

public class CpuMonitoringWorkerTests
{
    [Fact]
    public async Task SamplesWithoutHttpTraffic_UpdateLimitingMetricsAndLogEachTransitionOnce()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions());
        var metrics = new CpuMetrics(options);
        var registry = Metrics.NewCustomRegistry();
        var gauges = new DynamicGaugeService(Metrics.WithCustomRegistry(registry));
        var logger = new RecordingLogger();
        using var worker = new CpuMonitoringWorker(options, metrics, logger, gauges);

        worker.RecordCpuUsage(85);
        Assert.True(metrics.IsLimiting);
        Assert.Equal(1, gauges.GetGaugeValue("slimfaas_cpu_rate_limiting_active"));
        worker.RecordCpuUsage(90);
        worker.RecordCpuUsage(70);
        Assert.True(metrics.IsLimiting);
        worker.RecordCpuUsage(50);
        Assert.False(metrics.IsLimiting);
        Assert.Equal(0, gauges.GetGaugeValue("slimfaas_cpu_rate_limiting_active"));
        worker.RecordCpuUsage(70);
        Assert.False(metrics.IsLimiting);

        using var exposition = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(exposition);
        string text = Encoding.UTF8.GetString(exposition.ToArray());
        Assert.Contains("slimfaas_cpu_usage_percent 70\n", text);
        Assert.Contains("slimfaas_cpu_rate_limiting_active 0\n", text);
        Assert.DoesNotContain("slimfaas_cpu_usage_percent{", text);
        Assert.DoesNotContain("slimfaas_cpu_rate_limiting_active{", text);
        Assert.Single(logger.Messages, entry => entry.Level == LogLevel.Warning &&
            entry.Message.StartsWith("CPU rate limiting activated."));
        Assert.Single(logger.Messages, entry => entry.Level == LogLevel.Information &&
            entry.Message.StartsWith("CPU rate limiting deactivated."));
        Assert.Equal(2, logger.Messages.Count(entry => entry.Message.StartsWith("High CPU usage detected:")));
    }

    [Fact]
    public async Task DisabledMonitoring_DoesNotPublishSamplesOrActivateLimiting()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new RateLimitingOptions { Enabled = false });
        var metrics = new CpuMetrics(options);
        var registry = Metrics.NewCustomRegistry();
        var logger = new RecordingLogger();
        using var worker = new CpuMonitoringWorker(options, metrics, logger,
            new DynamicGaugeService(Metrics.WithCustomRegistry(registry)));

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.False(metrics.IsLimiting);
        using var exposition = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(exposition);
        Assert.DoesNotContain("slimfaas_cpu_", Encoding.UTF8.GetString(exposition.ToArray()));
        Assert.Contains(logger.Messages, entry => entry.Message == "CPU monitoring is disabled");
    }

    private sealed class RecordingLogger : ILogger<CpuMonitoringWorker>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add((logLevel, formatter(state, exception)));
    }
}
