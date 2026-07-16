using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimData;
using SlimData.Options;
using SlimFaas.Kubernetes;
using SlimFaas.Options;

namespace SlimFaas.Tests.Workers;

public sealed class SlimDataMembershipReconciliationWorkerTests
{
    private static readonly Uri LocalEndpoint = new("http://10.0.0.1:3262/");
    private static readonly Uri RemoteEndpoint = new("http://10.0.0.2:3262/");
    private static readonly Uri NewEndpoint = new("http://10.0.0.3:3262/");

    [Fact]
    public async Task Missing_follower_is_added_without_waiting_for_readiness()
    {
        var context = new TestContext(
            [LocalEndpoint],
            [Pod("slimfaas-0", "10.0.0.1"), Pod("slimfaas-1", "10.0.0.2")]);
        context.Coordinator
            .Setup(x => x.AddMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.AddMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()),
            Times.Once);
        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Missing_member_is_added_before_a_stale_member_is_considered_for_removal()
    {
        var staleEndpoint = new Uri("http://10.0.0.4:3262/");
        var context = new TestContext(
            [LocalEndpoint, staleEndpoint],
            [Pod("slimfaas-0", "10.0.0.1"), Pod("slimfaas-2", "10.0.0.3")]);
        context.Coordinator
            .Setup(x => x.AddMemberAsync(NewEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.AddMemberAsync(NewEndpoint, It.IsAny<CancellationToken>()),
            Times.Once);
        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Stale_member_is_removed_only_after_three_consecutive_missing_cycles()
    {
        var context = CreateStaleRemoteContext();
        context.Coordinator
            .Setup(x => x.RemoveMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Reappearing_member_resets_its_missing_cycle_counter()
    {
        var pods = new List<PodInformation> { Pod("slimfaas-0", "10.0.0.1") };
        var context = new TestContext([LocalEndpoint, RemoteEndpoint], pods);
        context.Coordinator
            .Setup(x => x.RemoveMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        pods.Add(Pod("slimfaas-1", "10.0.0.2"));
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        pods.RemoveAt(1);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Snapshot_without_the_local_member_never_removes_members()
    {
        var context = new TestContext(
            [LocalEndpoint, RemoteEndpoint],
            [Pod("slimfaas-1", "10.0.0.2")]);

        for (var cycle = 0; cycle < 5; cycle++)
            await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Membership_is_not_changed_without_leadership_consensus_and_lease(
        bool leadership,
        bool consensus,
        bool lease)
    {
        var context = CreateStaleRemoteContext(leadership, consensus, lease);

        for (var cycle = 0; cycle < 5; cycle++)
            await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.AddMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(It.IsAny<Uri>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Failed_removal_does_not_advance_missing_cycles_permanently()
    {
        var context = CreateStaleRemoteContext();
        context.Coordinator
            .SetupSequence(x => x.RemoveMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);
        await context.Worker.ReconcileOnceAsync(CancellationToken.None);

        context.Coordinator.Verify(
            x => x.RemoveMemberAsync(RemoteEndpoint, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    private static TestContext CreateStaleRemoteContext(
        bool leadership = true,
        bool consensus = true,
        bool lease = true)
        => new(
            [LocalEndpoint, RemoteEndpoint],
            [Pod("slimfaas-0", "10.0.0.1")],
            leadership,
            consensus,
            lease);

    private static PodInformation Pod(string name, string ip)
        => new(name, true, true, ip, "slimfaas", ServiceName: "slimfaas");

    private sealed class TestContext
    {
        internal TestContext(
            IReadOnlyCollection<Uri> currentMembers,
            IList<PodInformation> desiredPods,
            bool leadership = true,
            bool consensus = true,
            bool lease = true)
        {
            var replicas = new Mock<IReplicasService>(MockBehavior.Strict);
            replicas.SetupGet(x => x.Deployments).Returns(() => new DeploymentsInformations(
                [],
                new SlimFaasDeploymentInformation(desiredPods.Count, desiredPods),
                []));

            var raftMembers = currentMembers.Select(CreateMember).ToArray();
            var cluster = new Mock<IRaftHttpCluster>(MockBehavior.Strict);
            cluster.SetupGet(x => x.LocalMemberAddress).Returns(LocalEndpoint);
            cluster.SetupGet(x => x.LeadershipToken).Returns(
                leadership ? CancellationToken.None : new CancellationToken(canceled: true));
            cluster.SetupGet(x => x.ConsensusToken).Returns(
                consensus ? CancellationToken.None : new CancellationToken(canceled: true));
            var leaseToken = lease ? CancellationToken.None : new CancellationToken(canceled: true);
            cluster.Setup(x => x.TryGetLeaseToken(out leaseToken)).Returns(lease);
            cluster.As<IRaftCluster>()
                .SetupGet(x => x.Members)
                .Returns(raftMembers);

            Coordinator = new Mock<IClusterMembershipCoordinator>(MockBehavior.Strict);
            var namespaceProvider = new Mock<INamespaceProvider>(MockBehavior.Strict);
            namespaceProvider.SetupGet(x => x.CurrentNamespace).Returns("test");

            Worker = new SlimDataMembershipReconciliationWorker(
                replicas.Object,
                cluster.Object,
                Coordinator.Object,
                NullLogger<SlimDataMembershipReconciliationWorker>.Instance,
                Microsoft.Extensions.Options.Options.Create(
                    new SlimFaasOptions { BaseSlimDataUrl = "http://{pod_ip}:3262" }),
                Microsoft.Extensions.Options.Options.Create(
                    new WorkersOptions { ReplicasSynchronizationDelayMilliseconds = 1 }),
                Microsoft.Extensions.Options.Options.Create(
                    new SlimDataMembershipOptions { RemovalMissingCycles = 3 }),
                namespaceProvider.Object);
        }

        internal Mock<IClusterMembershipCoordinator> Coordinator { get; }

        internal SlimDataMembershipReconciliationWorker Worker { get; }

        private static IRaftClusterMember CreateMember(Uri endpoint)
        {
            var member = new Mock<IRaftClusterMember>(MockBehavior.Strict);
            member.SetupGet(x => x.EndPoint).Returns(new UriEndPoint(endpoint));
            return member.Object;
        }
    }
}
