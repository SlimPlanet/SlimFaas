using System.Reflection;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace SlimData.Tests;

/// <summary>
/// Exercises the actual dependency, including its internal replication coordinator.
/// Reflection is confined to these tests because DotNext does not expose the affected
/// helpers publicly; reproducing their algorithms here would not protect an upgrade.
/// </summary>
public sealed class DotNextRaftRegressionTests
{
    [Fact]
    public void Full_replication_queue_completes_the_dropped_members_barrier_as_unavailable()
    {
        var assembly = typeof(ConsensusOnlyState).Assembly;
        var barrierType = assembly.GetType(
            "DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.ReplicationBarrier", throwOnError: true)!;
        var processType = assembly.GetType(
            "DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.ReplicationProcess`1", throwOnError: true)!
            .MakeGenericType(typeof(IRaftClusterMember));
        using var process = (IDisposable)Activator.CreateInstance(
            processType, Mock.Of<IRaftClusterMember>(), 1)!;
        processType.GetProperty("Logger")!.SetValue(process, NullLogger.Instance);

        // Do not start the consumer: a capacity-one channel is deterministically full
        // after the first round, without networking, timeouts or scheduler races.
        var first = Activator.CreateInstance(barrierType)!;
        var rejected = Activator.CreateInstance(barrierType)!;
        var wait = barrierType.GetMethod("WaitAsync")!;
        _ = wait.Invoke(first, [1, 1L]);
        var completion = wait.Invoke(rejected, [1, 2L])!;
        var replicate = processType.GetMethod("Replicate")!;
        replicate.Invoke(process, [first]);
        replicate.Invoke(process, [rejected]);

        Assert.True((bool)completion.GetType().GetProperty("IsCompletedSuccessfully")!.GetValue(completion)!,
            "A full replication queue must report the member unavailable instead of losing its barrier response.");
        var awaiter = completion.GetType().GetMethod("GetAwaiter")!.Invoke(completion, null)!;
        var result = awaiter.GetType().GetMethod("GetResult")!.Invoke(awaiter, null)!;
        Assert.False((bool)result.GetType().GetProperty("HasConsensus")!.GetValue(result)!);
    }

    [Theory]
    [InlineData(2, 3, true)] // Newer term wins even with a shorter log.
    [InlineData(4, 1, false)] // A longer log cannot compensate for an older term.
    [InlineData(2, 2, false)]
    [InlineData(3, 2, true)]
    [InlineData(4, 2, true)]
    public async Task Elections_compare_last_log_term_before_log_length(long index, long term, bool expected)
    {
        using var state = new ConsensusOnlyState();
        IPersistentState auditTrail = state;
        for (var i = 0; i < 3; i++)
            await auditTrail.AppendAsync(new BinaryLogEntry { Term = 2, Content = new byte[] { 1 } });

        var method = typeof(PersistentStateExtensions).GetMethod(
            "IsUpToDateAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = await (ValueTask<bool>)method.Invoke(
            null, [auditTrail, index, term, CancellationToken.None])!;

        Assert.Equal(expected, result);
    }
}
