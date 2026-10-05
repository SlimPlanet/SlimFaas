using DotNext.Net.Cluster.Consensus.Raft;
using Moq;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed class SlimDataReadinessTests
{
    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public async Task Readiness_requires_a_leader_consensus_and_completed_warmup(
        bool hasLeader, bool hasConsensus, bool warmupCompleted, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            await using var state = new SlimPersistentState(root);
            var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
            var protocol = new Mock<ISlimDataProtocolCompatibility>(MockBehavior.Strict);
            protocol.SetupGet(x => x.IsCompatible).Returns(true);
            cluster.SetupGet(x => x.Readiness).Returns(warmupCompleted ? Task.CompletedTask : new TaskCompletionSource().Task);
            cluster.SetupGet(x => x.Leader).Returns(hasLeader ? Mock.Of<IRaftClusterMember>() : null);
            cluster.SetupGet(x => x.ConsensusToken).Returns(new CancellationToken(!hasConsensus));

            Assert.Equal(expected, SlimDataReadiness.IsReady(cluster.Object, state, protocol.Object));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Readiness_is_false_without_a_compatible_command_protocol()
    {
        var root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        try
        {
            await using var state = new SlimPersistentState(root);
            var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
            var protocol = new Mock<ISlimDataProtocolCompatibility>(MockBehavior.Strict);
            protocol.SetupGet(x => x.IsCompatible).Returns(false);

            Assert.False(SlimDataReadiness.IsReady(cluster.Object, state, protocol.Object));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
