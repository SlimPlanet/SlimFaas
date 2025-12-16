using ClusterFileDemoProdish.Models;
using ClusterFileDemoProdish.Storage;
using System.Text;
using System.Text.Json;

namespace ClusterFileDemoProdish.Workers;

public sealed class ExpiredFileCleanupWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<ExpiredFileCleanupWorker> _logger;

    public ExpiredFileCleanupWorker(IServiceProvider sp, ILogger<ExpiredFileCleanupWorker> logger)
    {
        _sp = sp;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var kv = scope.ServiceProvider.GetRequiredService<IKvStore>();
                var files = scope.ServiceProvider.GetRequiredService<IFileRepository>();

                if (kv is SlimDataKvStore slim)
                {
                    var snapshot = slim.Snapshot();
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    foreach (var (key, entry) in snapshot)
                    {
                        if (!key.StartsWith("filemeta:", StringComparison.Ordinal)) continue;

                        try
                        {
                            var meta = JsonSerializer.Deserialize<FileMeta>(Encoding.UTF8.GetString(entry.Value));
                            if (meta is null) continue;

                            if (meta.ExpiresUtcMs is long exp && now >= exp)
                            {
                                await kv.DeleteAsync(key);
                                await files.DeleteAsync(meta.Id, stoppingToken);
                            }
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cleanup worker failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }
}
