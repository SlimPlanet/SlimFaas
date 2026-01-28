using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;

namespace SlimFaas.Database;

public interface ISlimDataStatus
{
    Task WaitForReadyAsync();
}

public class SlimDataMock : ISlimDataStatus
{
    public async Task WaitForReadyAsync() => await Task.CompletedTask;
}

public class SlimDataStatus(IRaftCluster cluster, ILogger<SlimDataStatus> logger) : ISlimDataStatus
{
    public async Task WaitForReadyAsync()
    {
        IRaftCluster raftCluster = cluster;

        while (raftCluster.Leader == null)
        {
            logger.LogWarning("Raft cluster has no leader, waiting...");
            await Task.Delay(500);
        }
    }
}
