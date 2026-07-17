using DotNext.Net.Cluster.Consensus.Raft;
using Moq;
using SlimData;
using SlimFaas;
using Xunit;

namespace SlimFaas.Tests;

public sealed class MasterSlimDataServiceTests
{
    [Fact]
    public void IsMaster_requires_leadership_consensus_and_an_active_lease()
    {
        var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
        var leaseToken = CancellationToken.None;
        cluster.SetupGet(x => x.LeadershipToken).Returns(CancellationToken.None);
        cluster.SetupGet(x => x.ConsensusToken).Returns(CancellationToken.None);
        cluster.Setup(x => x.TryGetLeaseToken(out leaseToken)).Returns(true);
        var protocol = CompatibleProtocol();

        var service = new MasterSlimDataService(cluster.Object, protocol.Object);

        Assert.True(service.IsMaster);
    }

    [Fact]
    public void IsMaster_is_false_without_consensus()
    {
        var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
        cluster.SetupGet(x => x.LeadershipToken).Returns(CancellationToken.None);
        cluster.SetupGet(x => x.ConsensusToken).Returns(new CancellationToken(canceled: true));
        var protocol = CompatibleProtocol();

        var service = new MasterSlimDataService(cluster.Object, protocol.Object);

        Assert.False(service.IsMaster);
    }

    [Fact]
    public void IsMaster_is_false_without_an_active_lease()
    {
        var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
        var leaseToken = new CancellationToken(canceled: true);
        cluster.SetupGet(x => x.LeadershipToken).Returns(CancellationToken.None);
        cluster.SetupGet(x => x.ConsensusToken).Returns(CancellationToken.None);
        cluster.Setup(x => x.TryGetLeaseToken(out leaseToken)).Returns(true);
        var protocol = CompatibleProtocol();

        var service = new MasterSlimDataService(cluster.Object, protocol.Object);

        Assert.False(service.IsMaster);
    }

    [Fact]
    public void IsMaster_is_false_when_the_command_protocol_is_incompatible()
    {
        var cluster = new Mock<IRaftCluster>(MockBehavior.Strict);
        var protocol = new Mock<ISlimDataProtocolCompatibility>(MockBehavior.Strict);
        protocol.SetupGet(x => x.IsCompatible).Returns(false);

        var service = new MasterSlimDataService(cluster.Object, protocol.Object);

        Assert.False(service.IsMaster);
    }

    private static Mock<ISlimDataProtocolCompatibility> CompatibleProtocol()
    {
        var protocol = new Mock<ISlimDataProtocolCompatibility>(MockBehavior.Strict);
        protocol.SetupGet(x => x.IsCompatible).Returns(true);
        return protocol;
    }
}
