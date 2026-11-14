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
        Assembly assembly = typeof(ConsensusOnlyState).Assembly;
        Type barrierType = Require(assembly.GetType(
            "DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.ReplicationBarrier"), "ReplicationBarrier type");
        Type processType = Require(assembly.GetType(
            "DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.ReplicationProcess`1"), "ReplicationProcess<T> type")
            .MakeGenericType(typeof(IRaftClusterMember));
        using var process = (IDisposable)Require(Activator.CreateInstance(
            processType, Mock.Of<IRaftClusterMember>(), 1), "ReplicationProcess constructor");
        Require(processType.GetProperty("Logger", BindingFlags.Instance | BindingFlags.Public), "Logger property")
            .SetValue(process, NullLogger.Instance);

        // Do not start the consumer: a capacity-one channel is deterministically full
        // after the first round, without networking, timeouts or scheduler races.
        object first = Require(Activator.CreateInstance(barrierType), "ReplicationBarrier constructor");
        object rejected = Require(Activator.CreateInstance(barrierType), "ReplicationBarrier constructor");
        MethodInfo wait = Require(barrierType.GetMethod("WaitAsync", BindingFlags.Instance | BindingFlags.Public), "WaitAsync method");
        _ = wait.Invoke(first, [1, 1L]);
        object completion = Require(wait.Invoke(rejected, [1, 2L]), "WaitAsync result");
        MethodInfo replicate = Require(processType.GetMethod("Replicate", BindingFlags.Instance | BindingFlags.Public), "Replicate method");
        replicate.Invoke(process, [first]);
        replicate.Invoke(process, [rejected]);

        Assert.True(Assert.IsType<bool>(Require(completion.GetType().GetProperty("IsCompletedSuccessfully"),
                "ValueTask.IsCompletedSuccessfully property").GetValue(completion)),
            "A full replication queue must report the member unavailable instead of losing its barrier response.");
        object awaiter = Require(Require(completion.GetType().GetMethod("GetAwaiter"), "ValueTask.GetAwaiter method")
            .Invoke(completion, null), "ValueTask awaiter");
        object result = Require(Require(awaiter.GetType().GetMethod("GetResult"), "ValueTaskAwaiter.GetResult method")
            .Invoke(awaiter, null), "ReplicationBarrier result");
        Assert.False(Assert.IsType<bool>(Require(result.GetType().GetProperty("HasConsensus"),
            "HasConsensus property").GetValue(result)));
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

        MethodInfo method = Require(typeof(PersistentStateExtensions).GetMethod(
            "IsUpToDateAsync", BindingFlags.Static | BindingFlags.NonPublic), "IsUpToDateAsync method");
        bool result = await Assert.IsType<ValueTask<bool>>(method.Invoke(
            null, [auditTrail, index, term, CancellationToken.None]));

        Assert.Equal(expected, result);
    }

    private static T Require<T>(T? binding, string name) where T : class
        => binding ?? throw new Xunit.Sdk.XunitException(
            $"DotNext dependency contract changed: missing {name}. Revisit the upstream #300 " +
            "replication/election regression binding before interpreting this as a Raft failure.");
}
