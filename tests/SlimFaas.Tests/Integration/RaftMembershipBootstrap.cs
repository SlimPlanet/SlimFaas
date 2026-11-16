using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimFaas.Tests.Integration;

internal static class RaftMembershipBootstrap
{
    internal static async Task SeedAsync(Func<Task> write, Action leadershipChanged, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(leadershipChanged);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await write();
                return;
            }
            catch (NotLeaderException) when (!token.IsCancellationRequested)
            {
                // Only the fixture's idempotent seed uses this helper. The original
                // deadline still applies, and later recovery writes are not retried.
                leadershipChanged();
            }
        }
    }
}
