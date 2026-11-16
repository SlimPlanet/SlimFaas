using DotNext.Net.Cluster.Consensus.Raft;

namespace SlimFaas.Tests.Integration;

public sealed class RaftMembershipBootstrapTests
{
    [Fact]
    public async Task Seed_reselects_after_a_bootstrap_leadership_change()
    {
        int attempts = 0;
        int changes = 0;

        await RaftMembershipBootstrap.SeedAsync(
            () => ++attempts == 1
                ? Task.FromException(new NotLeaderException(new OperationCanceledException()))
                : Task.CompletedTask,
            () => changes++,
            CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Seed_propagates_other_failures_without_retrying()
    {
        int attempts = 0;
        var failure = new InvalidOperationException("Bootstrap failure unrelated to leadership.");

        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RaftMembershipBootstrap.SeedAsync(
                () =>
                {
                    attempts++;
                    return Task.FromException(failure);
                },
                () => Assert.Fail("An unrelated failure must not be retried."),
                CancellationToken.None));

        Assert.Same(failure, observed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task Seed_does_not_start_after_the_fixture_deadline()
    {
        using var deadline = new CancellationTokenSource();
        await deadline.CancelAsync();
        int attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RaftMembershipBootstrap.SeedAsync(
                () =>
                {
                    attempts++;
                    return Task.CompletedTask;
                },
                () => Assert.Fail("An expired setup must not be retried."),
                deadline.Token));

        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Seed_does_not_retry_when_the_deadline_expires_during_a_rejected_write()
    {
        using var deadline = new CancellationTokenSource();
        int attempts = 0;
        int changes = 0;
        var failure = new NotLeaderException();

        NotLeaderException observed = await Assert.ThrowsAsync<NotLeaderException>(() =>
            RaftMembershipBootstrap.SeedAsync(
                async () =>
                {
                    attempts++;
                    await deadline.CancelAsync();
                    throw failure;
                },
                () => changes++,
                deadline.Token));

        Assert.Same(failure, observed);
        Assert.Equal(1, attempts);
        Assert.Equal(0, changes);
    }
}
