using DotNext.IO.Log;
using System.Buffers.Binary;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class DurableWriteAheadLogTests
{
    [Theory]
    [InlineData(32)]
    [InlineData(128)]
    public async Task Concurrent_raft_appends_all_finish_with_a_durable_checkpoint(int count)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync(deadline.Token);
            await using var wal = new DurableWriteAheadLog(new WriteAheadLog.Options
            {
                Location = Path.Combine(root, "wal")
            }, state);
            IPersistentState raftLog = wal;
            Task[] appends = Enumerable.Range(0, count).Select(_ =>
                raftLog.AppendAsync(new BinaryLogEntry { Term = 1, Content = new byte[] { 42 } }, deadline.Token).AsTask())
                .ToArray();
            await Task.WhenAll(appends);
            Assert.Equal(count, wal.LastEntryIndex);
            await using var checkpoint = new FileStream(Path.Combine(root, "wal", "checkpoint"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            var header = new byte[20];
            await checkpoint.ReadExactlyAsync(header, deadline.Token);
            Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(header));
            Assert.Equal(count, BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(12)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Commit_and_explicit_flush_share_the_pending_append_flush_observer(int variant)
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
                entered.TrySetResult();
                return release.Task.WaitAsync(token);
            });
            IPersistentState raftLog = wal;
            Task append = AppendAsync(raftLog, 0, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            Task observer = variant == 0 ? raftLog.CommitAsync(1, deadline.Token).AsTask() : wal.FlushAsync(deadline.Token);
            try
            {
                // An append observer already owns the single-consumer completion.
                // A commit can need that same completion even for an earlier entry.
                Assert.False(observer.IsCompleted);
            }
            finally
            {
                release.TrySetResult();
                await Task.WhenAll(append, observer);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(WriteAheadLog.MemoryManagementStrategy.SharedMemory)]
    [InlineData(WriteAheadLog.MemoryManagementStrategy.PrivateMemory)]
    public async Task Appending_committing_and_flushing_a_busy_log_can_overlap(WriteAheadLog.MemoryManagementStrategy strategy)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync(deadline.Token);
            await using var wal = new DurableWriteAheadLog(new WriteAheadLog.Options
            {
                Location = Path.Combine(root, "wal"),
                MemoryManagement = strategy
            }, state);
            IPersistentState raftLog = wal;
            var payload = new byte[65536];
            Task[] producers = Enumerable.Range(0, 4).Select(async _ =>
            {
                for (int i = 0; i < 128; i++)
                {
                    await raftLog.AppendAsync(new LogEntry<AddKeyValueCommand>
                    {
                        Term = 1,
                        Command = new AddKeyValueCommand { Key = "busy", Value = payload, Operation = KeyValueOperation.Set }
                    }, deadline.Token);
                    await raftLog.CommitAsync(raftLog.LastEntryIndex, deadline.Token);
                    await wal.FlushAsync(deadline.Token);
                }
            }).ToArray();
            await Task.WhenAll(producers);
            Assert.Equal(512, wal.LastCommittedEntryIndex);
            await wal.WaitForApplyAsync(512, deadline.Token);
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
