using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimFaas.Tests.Integration;

public sealed class RaftMembershipObservationTests
{
    [Fact]
    public async Task A_new_leader_can_continue_observing_the_same_desired_membership()
    {
        int calls = 0;
        int changes = 0;
        await RaftMembershipObservation.RunAsync(
            () => ++calls == 1 ? Task.FromException(new NotLeaderException()) : Task.CompletedTask,
            () => true, 6, () => changes++, CancellationToken.None);
        Assert.Equal(7, calls);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Observation_continues_until_all_remaining_members_converge()
    {
        int calls = 0;
        await RaftMembershipObservation.RunAsync(() => { calls++; return Task.CompletedTask; },
            () => calls >= 4, 2, () => Assert.Fail("No leadership change injected."), CancellationToken.None);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Other_membership_failures_are_not_swallowed()
    {
        var failure = new InvalidOperationException("Unrelated membership failure");
        Exception observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RaftMembershipObservation.RunAsync(() => Task.FromException(failure), () => false, 6,
                () => Assert.Fail("An unrelated failure cannot select another leader."), CancellationToken.None));
        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task Expired_deadline_does_not_start_an_observation()
    {
        using var deadline = new CancellationTokenSource();
        await deadline.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RaftMembershipObservation.RunAsync(() => { Assert.Fail("Deadline already expired."); return Task.CompletedTask; },
                () => false, 6, () => Assert.Fail("Deadline already expired."), deadline.Token));
    }

    [Fact]
    public async Task Deadline_expiry_during_a_leadership_change_is_not_retried()
    {
        using var deadline = new CancellationTokenSource();
        var failure = new NotLeaderException();
        Exception observed = await Assert.ThrowsAsync<NotLeaderException>(() =>
            RaftMembershipObservation.RunAsync(async () => { await deadline.CancelAsync(); throw failure; },
                () => false, 6, () => Assert.Fail("An expired observation cannot be retried."), deadline.Token));
        Assert.Same(failure, observed);
    }
}
