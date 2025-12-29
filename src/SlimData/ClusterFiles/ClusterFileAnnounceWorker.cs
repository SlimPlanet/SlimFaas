// ClusterFileAnnounceWorker.cs
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SlimData.ClusterFiles;

public sealed class ClusterFileAnnounceWorker : BackgroundService
{
    private readonly ClusterFileAnnounceQueue _queue;
    private readonly IClusterFileSync _sync;
    private readonly IFileRepository _repo;
    private readonly ILogger<ClusterFileAnnounceWorker> _logger;

    public ClusterFileAnnounceWorker(
        ClusterFileAnnounceQueue queue,
        IClusterFileSync sync,
        IFileRepository repo,
        ILogger<ClusterFileAnnounceWorker> logger)
    {
        _queue = queue;
        _sync = sync;
        _repo = repo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var a in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                // Déjà présent => rien à faire
                if (await _repo.ExistsAsync(a.Id, a.Sha256Hex, stoppingToken).ConfigureAwait(false))
                    continue;

                // Pull => récupère depuis un des nœuds
                var res = await _sync.PullFileIfMissingAsync(a.Id, a.Sha256Hex, stoppingToken).ConfigureAwait(false);
                if (res.Stream is null)
                {
                    _logger.LogWarning("Auto-pull failed. Id={Id} Sha={Sha}", a.Id, a.Sha256Hex);
                    continue;
                }
                // IMPORTANT: on n'a pas besoin du stream ici => on le ferme pour éviter les fuites
                if(res.Stream != null)
                {
                    await res.Stream.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to auto-pull announced file. Id={Id}", a.Id);
            }
        }
    }
}