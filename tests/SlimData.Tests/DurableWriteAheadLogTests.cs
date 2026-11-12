using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class DurableWriteAheadLogTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Raft_append_cannot_acknowledge_before_the_durability_barrier(int variant)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync(deadline.Token);
            await using var wal = new DurableWriteAheadLog(new WriteAheadLog.Options
            {
                Location = Path.Combine(root, "wal")
            }, state, token =>
            {
                entered.SetResult();
                return release.Task.WaitAsync(token);
            });
            IPersistentState raftLog = wal;
            Task append = AppendAsync(raftLog, variant, deadline.Token);
            await Task.WhenAny(entered.Task, append).WaitAsync(deadline.Token);
            try
            {
                Assert.True(entered.Task.IsCompletedSuccessfully);
                Assert.False(append.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await append;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Raft_append_propagates_a_failed_flush_instead_of_acknowledging(int variant)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync();
            var failure = new IOException("The isolated WAL filesystem is full.");
            await using var wal = new DurableWriteAheadLog(new WriteAheadLog.Options
            {
                Location = Path.Combine(root, "wal")
            }, state, _ => Task.FromException(failure));
            IOException actual = await Assert.ThrowsAsync<IOException>(() =>
                AppendAsync(wal, variant, CancellationToken.None));
            Assert.Same(failure, actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Raft_append_preserves_cancellation_while_waiting_for_a_durable_checkpoint(int variant)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync(deadline.Token);
            await using var wal = new DurableWriteAheadLog(new WriteAheadLog.Options
            {
                Location = Path.Combine(root, "wal")
            }, state, token =>
            {
                entered.SetResult();
                return pending.Task.WaitAsync(token);
            });
            Task append = AppendAsync(wal, variant, cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(deadline.Token);
                await cancellation.CancelAsync();
                OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => append);
                Assert.Equal(cancellation.Token, error.CancellationToken);
            }
            finally
            {
                pending.TrySetResult();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AppendAsync(IPersistentState wal, int variant, CancellationToken token)
    {
        LogEntry<AddKeyValueCommand> entry = new()
        {
            Term = 1,
            Command = new AddKeyValueCommand { Key = "confirmed", Value = new byte[] { 42 }, Operation = KeyValueOperation.Set }
        };
        switch (variant)
        {
            case 0:
                await wal.AppendAsync(entry, token);
                break;
            case 1:
                await wal.AppendAsync(entry, 1, token);
                break;
            case 2:
            {
                await using var producer = LogEntryProducer<LogEntry<AddKeyValueCommand>>.Of(entry);
                await wal.AppendAsync(producer, 1, skipCommitted: true, token);
                break;
            }
        }
    }
}
