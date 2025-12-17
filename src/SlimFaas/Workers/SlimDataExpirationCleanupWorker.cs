using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SlimData.Expiration;

public sealed class SlimDataExpirationCleanupWorker : BackgroundService
{
    private readonly SlimDataExpirationCleaner _cleaner;
    private readonly ILogger<SlimDataExpirationCleanupWorker> _logger;
    private readonly TimeSpan _interval;

    public SlimDataExpirationCleanupWorker(
        SlimDataExpirationCleaner cleaner,
        ILogger<SlimDataExpirationCleanupWorker> logger,
        TimeSpan? interval = null)
    {
        _cleaner = cleaner;
        _logger = logger;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // petit jitter pour éviter que tous les noeuds tapent en même temps
        try { await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10000, 20000)), stoppingToken); }
        catch { /* ignore */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _cleaner.CleanupOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SlimData TTL cleanup cycle failed.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
