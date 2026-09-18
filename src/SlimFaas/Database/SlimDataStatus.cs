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
    ISlimDataProtocolCompatibility protocolCompatibility,
    ILogger<SlimDataStatus> logger) : ISlimDataStatus
{
    public async Task WaitForReadyAsync()
    {
        await cluster.Readiness.ConfigureAwait(false);

        while (cluster.Leader is null ||
               cluster.ConsensusToken.IsCancellationRequested ||
               persistentState.IsRestoring ||
               !protocolCompatibility.IsCompatible)
        {
            logger.LogRaftClusterIsNotReadyWaiting(protocolCompatibility.Reason);
            await Task.Delay(500).ConfigureAwait(false);
        }
    }
}
