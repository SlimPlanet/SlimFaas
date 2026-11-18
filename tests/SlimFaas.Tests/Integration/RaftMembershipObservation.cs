using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimFaas.Tests.Integration;

internal static class RaftMembershipObservation
{
    internal static async Task RunAsync(Func<Task> reconcile, Func<bool> converged,
        int minimumObservations, Action leadershipChanged, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reconcile);
        ArgumentNullException.ThrowIfNull(converged);
        ArgumentNullException.ThrowIfNull(leadershipChanged);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumObservations, 1);
        int completed = 0;
        while (completed < minimumObservations || !converged())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await reconcile();
                completed++;
            }
            catch (NotLeaderException) when (!token.IsCancellationRequested)
            {
                // Desired membership is idempotent. The next observation selects
                // the current leader, matching the production background workers.
                // This helper never receives an application-write callback.
                leadershipChanged();
            }
            if (completed < minimumObservations || !converged())
                await Task.Delay(25, token); // Same cadence as this fixture's existing UntilAsync.
        }
    }
}
