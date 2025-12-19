// ClusterFileSyncBootstrapper.cs
using Microsoft.Extensions.Hosting;

namespace SlimData.ClusterFiles;

public sealed class ClusterFileSyncBootstrapper : IHostedService
{
    // Le simple fait d’injecter IClusterFileSync force sa création,
    // donc l’enregistrement du listener (AddListener) dès le démarrage.
    public ClusterFileSyncBootstrapper(IClusterFileSync _)
    {
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}