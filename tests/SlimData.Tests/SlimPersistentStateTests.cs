using System.Text;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class SlimPersistentStateTests
{
    [Fact]
    public async Task WriteAheadLog_restores_key_values_hashsets_and_queues_from_snapshot()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using (var state = new SlimPersistentState(root))
            await using (var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state))
            {
                await AppendCommitWaitAsync(wal, new AddKeyValueCommand
                {
                    Operation = KeyValueOperation.Set,
                    Key = "snapshot-key",
                    Value = Encoding.UTF8.GetBytes("snapshot-value")
                });

                await AppendCommitWaitAsync(wal, new AddHashSetCommand
                {
                    Key = "snapshot-hash",
                    Value = new Dictionary<string, ReadOnlyMemory<byte>>
                    {
                        ["field"] = Encoding.UTF8.GetBytes("hash-value")
                    }
                });

                await AppendCommitWaitAsync(wal, new ListLeftPushBatchCommand
                {
                    Items =
                    [
                        new ListLeftPushBatchCommand.BatchItem
                        {
                            Key = "snapshot-queue",
                            Identifier = "queue-item-1",
                            NowTicks = DateTime.UtcNow.Ticks,
                            RetryTimeout = 30,
                            Retries = [1, 2],
                            HttpStatusCodesWorthRetrying = [500],
                            Value = Encoding.UTF8.GetBytes("queue-value")
                        }
                    ]
                });

                for (var i = 4; i <= 1000; i++)
                {
                    await AppendCommitWaitAsync(wal, new AddKeyValueCommand
                    {
                        Operation = KeyValueOperation.Set,
                        Key = "snapshot-filler",
                        Value = Encoding.UTF8.GetBytes(i.ToString())
                    });
                }

                await AppendCommitWaitAsync(wal, new AddKeyValueCommand
                {
                    Operation = KeyValueOperation.Set,
                    Key = "post-snapshot-key",
                    Value = Encoding.UTF8.GetBytes("post-snapshot-value")
                });

                await wal.FlushAsync(CancellationToken.None);
            }

            await using (var restoredState = new SlimPersistentState(root))
            await using (var restoredWal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, restoredState))
            {
                await restoredState.RestoreAsync(CancellationToken.None);
                await restoredWal.InitializeAsync(CancellationToken.None);
                using var replayTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var committedIndex = restoredWal.LastCommittedEntryIndex;
                if (committedIndex > 0L)
                    await restoredWal.WaitForApplyAsync(committedIndex, replayTimeout.Token);

                Assert.Equal(
                    "snapshot-value",
                    Encoding.UTF8.GetString(restoredState.SlimDataState.KeyValues["snapshot-key"].Span));
                Assert.Equal(
                    "post-snapshot-value",
                    Encoding.UTF8.GetString(restoredState.SlimDataState.KeyValues["post-snapshot-key"].Span));

                var hash = restoredState.SlimDataState.Hashsets["snapshot-hash"];
                Assert.Equal("hash-value", Encoding.UTF8.GetString(hash["field"].Span));

                var queue = restoredState.SlimDataState.Queues["snapshot-queue"];
                var item = Assert.Single(queue);
                Assert.Equal("queue-item-1", item.Id);
                Assert.Equal("queue-value", Encoding.UTF8.GetString(item.Value.Span));
                Assert.Equal(30, item.HttpTimeoutSeconds);
                Assert.Equal([1, 2], item.TimeoutRetriesSeconds.ToArray());
                Assert.Contains(500, item.HttpStatusRetries);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AppendCommitWaitAsync<TCommand>(WriteAheadLog wal, TCommand command)
        where TCommand : struct, ICommand<TCommand>
    {
        var index = await wal.AppendAsync(command, token: CancellationToken.None);
        await wal.CommitAsync(index, CancellationToken.None);
        await wal.WaitForApplyAsync(index, CancellationToken.None);
    }

    private static string GetTemporaryDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }
}
