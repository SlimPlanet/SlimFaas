using DotNext.Net.Cluster.Consensus.Raft;
using SlimData;

namespace SlimFaas.Database;

internal static class SlimDataReadiness
{
    internal static bool IsReady(
        IRaftCluster? cluster,
        SlimPersistentState? persistentState,
        ISlimDataProtocolCompatibility? protocolCompatibility)
    {
        if (protocolCompatibility is null || !protocolCompatibility.IsCompatible)
            return false;

        if (cluster is null || persistentState is null)
            return false;

        try
        {
            return cluster.Readiness.IsCompletedSuccessfully &&
                   cluster.Leader is not null &&
                   !cluster.ConsensusToken.IsCancellationRequested &&
                   !persistentState.IsRestoring;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
