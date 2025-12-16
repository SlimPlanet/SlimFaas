using ClusterFileDemoProdish.Cluster;
using ClusterFileDemoProdish.Options;
using ClusterFileDemoProdish.Storage;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace ClusterFileDemoProdish.Workers;

/// <summary>
/// On startup (and periodically) pull meta list from leader, merge locally,
/// and ensure missing files are pulled.
/// </summary>
public sealed class StartupSyncWorker : BackgroundService
{
    private readonly IClusterFileSync _sync;
    private readonly IKvStore _kv;
    private readonly IFileRepository _files;
    private readonly ILogger<StartupSyncWorker> _logger;
    private readonly FileStorageOptions _opt;

    public StartupSyncWorker(
        IClusterFileSync sync,
        IKvStore kv,
        IFileRepository files,
        IOptions<FileStorageOptions> opt,
        ILogger<StartupSyncWorker> logger)
    {
        _sync = sync;
        _kv = kv;
        _files = files;
        _logger = logger;
        _opt = opt.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the cluster a moment to elect a leader.
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var metas = await _sync.PullMetaDumpFromLeaderAsync(stoppingToken);

                foreach (var meta in metas)
                {
                    if (meta.IsExpired) continue;
                    var json = JsonSerializer.Serialize(meta);
                    await _kv.SetAsync($"filemeta:{meta.Id}", Encoding.UTF8.GetBytes(json), timeToLiveMilliseconds: null);
                }

                // Pull missing files with bounded concurrency.
                var semaphore = new SemaphoreSlim(Math.Max(1, _opt.PullConcurrency));
                var tasks = metas.Select(async meta =>
                {
                    await semaphore.WaitAsync(stoppingToken);
                    try
                    {
                        var (exists, _) = await _files.TryGetAsync(meta.Id, stoppingToken);
                        if (!exists)
                            await _sync.PullFileIfMissingAsync(meta.Id, stoppingToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }).ToArray();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Startup sync iteration failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
