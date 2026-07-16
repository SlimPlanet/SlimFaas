using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimFaas;

public interface IMasterService
{
    bool IsMaster { get; }
}

public class MasterSlimDataService(IRaftCluster cluster) : IMasterService
{
    public bool IsMaster
    {
        get
        {
            try
            {
                return !cluster.LeadershipToken.IsCancellationRequested &&
                       !cluster.ConsensusToken.IsCancellationRequested &&
                       cluster.TryGetLeaseToken(out var leaseToken) &&
                       !leaseToken.IsCancellationRequested;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public Task CheckAsync() => Task.CompletedTask;
}
