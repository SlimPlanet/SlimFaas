using DotNext.Net.Cluster.Consensus.Raft;
using Moq;
using SlimData;
using SlimFaas.Database;

namespace SlimFaas.Tests.Database;

public sealed class SlimDataReadinessTests
{
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
