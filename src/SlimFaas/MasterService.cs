using DotNext.Net.Cluster.Consensus.Raft;
using SlimData;

namespace SlimFaas;

public interface IMasterService
{
    bool IsMaster { get; }
}

public class MasterSlimDataService(
    IRaftCluster cluster,
    ISlimDataProtocolCompatibility protocolCompatibility) : IMasterService
{
    public bool IsMaster
    {
        get
        {
            try
            {
                return protocolCompatibility.IsCompatible &&
                       !cluster.LeadershipToken.IsCancellationRequested &&
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
