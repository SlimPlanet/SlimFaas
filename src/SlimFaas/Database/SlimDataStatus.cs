using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using SlimData;

namespace SlimFaas.Database;

public interface ISlimDataStatus
{
    Task WaitForReadyAsync();
}

public class SlimDataMock : ISlimDataStatus
{
    public async Task WaitForReadyAsync() => await Task.CompletedTask;
}

public class SlimDataStatus(
    IRaftCluster cluster,
    SlimPersistentState persistentState,
    ILogger<SlimDataStatus> logger) : ISlimDataStatus
{
    public async Task WaitForReadyAsync()
    {
        await cluster.Readiness.ConfigureAwait(false);

        while (cluster.Leader is null || cluster.ConsensusToken.IsCancellationRequested || persistentState.IsRestoring)
        {
            logger.LogWarning("Raft cluster is not ready, waiting for leader, consensus and local state...");
            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}
