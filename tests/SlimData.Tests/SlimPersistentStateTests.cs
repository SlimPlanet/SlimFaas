using System.Text;
using System.Reflection;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Configuration;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class SlimPersistentStateTests
{
    [Fact]
    public async Task ApplyAsync_ignores_configuration_entries()
    {
        var root = GetTemporaryDirectory();

        try
        {
            await using var state = new SlimPersistentState(root);
            var entry = CreateStateMachineEntry(new InvalidConfigurationLogEntry(), index: 1L, isConfiguration: true);
            Assert.True(entry.IsConfiguration);

            var shouldSnapshot = await InvokeApplyAsync(state, entry);

            Assert.False(shouldSnapshot);
            Assert.Empty(state.SlimDataState.KeyValues);
            Assert.Empty(state.SlimDataState.Hashsets);
            Assert.Empty(state.SlimDataState.Queues);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAheadLog_restores_key_values_hashsets_and_queues_from_snapshot()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using (var state = CreateState(root, snapshotIntervalEntries: 100))
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

                for (var i = 4; i <= 100; i++)
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
                Assert.True(state.LastSnapshotSizeBytes > 0L);
            }

            await using (var restoredState = CreateState(root, snapshotIntervalEntries: 100))
            {
                // DotNext starts applying as soon as WriteAheadLog is constructed. Restore the
                // snapshot first so entries committed after it cannot be applied and then overwritten.
                await restoredState.RestoreAsync(CancellationToken.None);
                await using var restoredWal = new WriteAheadLog(
                    new WriteAheadLog.Options { Location = walPath },
                    restoredState);
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

    [Fact]
    public async Task Restore_invalid_snapshot_clears_old_state_and_restoring_flag()
    {
        var root = GetTemporaryDirectory();
        var snapshotPath = Path.Combine(root, "invalid.snapshot");

        try
        {
            await File.WriteAllBytesAsync(snapshotPath, BitConverter.GetBytes(-1));
            await using var state = new SlimPersistentState(root);
            state.SlimDataState.KeyValues = state.SlimDataState.KeyValues.SetItem(
                "old-key",
                Encoding.UTF8.GetBytes("old-value"));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                InvokeRestoreAsync(state, new FileInfo(snapshotPath), CancellationToken.None));

            Assert.False(state.IsRestoring);
            Assert.Empty(state.SlimDataState.KeyValues);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_continues_snapshot_cadence_from_restored_index()
    {
        var root = GetTemporaryDirectory();
        var snapshotPath = Path.Combine(root, "100-1");

        try
        {
            // Three empty collection counts in the existing snapshot binary format.
            await File.WriteAllBytesAsync(snapshotPath, new byte[3 * sizeof(int)]);
            await using var state = CreateState(root, snapshotIntervalEntries: 100);
            await InvokeRestoreAsync(state, new FileInfo(snapshotPath), CancellationToken.None);

            Assert.False(await ApplySetAsync(state, index: 101L, key: "first"));
            Assert.True(await ApplySetAsync(state, index: 200L, key: "second"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_skips_incompatible_key_value_entry()
    {
        var root = GetTemporaryDirectory();

        try
        {
            await using var state = new SlimPersistentState(root);
            var result = new KeyValueCommandResult();
            var context = new KeyValueCommandBatchContext([result]);
            var entry = CreateStateMachineEntry(
                new LegacyAddKeyValueLogEntry([(byte)KeyValueOperation.Set]),
                index: 2L,
                context: context);

            var shouldSnapshot = await InvokeApplyAsync(state, entry);

            Assert.True(shouldSnapshot);
            Assert.Empty(state.SlimDataState.KeyValues);
            Assert.Equal(KeyValueCommandStatus.NotCommitted, result.Status);
            Assert.Equal("Skipped incompatible SlimData Raft log entry.", result.ErrorMessage);

            var secondEntry = CreateStateMachineEntry(
                new LegacyAddKeyValueLogEntry([(byte)KeyValueOperation.Set]),
                index: 3L);
            Assert.False(await InvokeApplyAsync(state, secondEntry));

            var skipped = Assert.Single(state.GetSkippedCommandMetrics());
            Assert.Equal(AddKeyValueCommand.Id, skipped.CommandId);
            Assert.Equal(2L, skipped.Count);
            Assert.Equal(2L, state.GetRaftSafetyMetrics().FormatViolations);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_skips_malformed_non_key_value_entries_and_marks_context()
    {
        var root = GetTemporaryDirectory();

        try
        {
            await using var state = new SlimPersistentState(root);
            var context = new CommandApplyContext();
            var entry = CreateStateMachineEntry(
                new InvalidApplicationLogEntry(AddHashSetCommand.Id, [0]),
                index: 2L,
                context: context);

            Assert.True(await InvokeApplyAsync(state, entry));
            await context.WaitAsync(CancellationToken.None);

            Assert.True(context.IsSkipped);
            Assert.Equal("Skipped incompatible SlimData Raft log entry.", context.ErrorMessage);
            Assert.Empty(state.SlimDataState.Hashsets);
            Assert.Equal(1L, state.GetRaftSafetyMetrics().FormatViolations);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_skips_unknown_and_oversized_entries_without_reading_their_payload()
    {
        var root = GetTemporaryDirectory();

        try
        {
            await using var state = new SlimPersistentState(root);
            var unknown = CreateStateMachineEntry(
                new InvalidApplicationLogEntry(999, [0]),
                index: 2L);
            var oversized = CreateStateMachineEntry(
                new InvalidApplicationLogEntry(
                    ListCallbackCommand.Id,
                    [0],
                    declaredLength: (32L * 1024L * 1024L) + 1L),
                index: 3L);
            var negativeLength = CreateStateMachineEntry(
                new InvalidApplicationLogEntry(
                    ListCallbackBatchCommand.Id,
                    [0],
                    declaredLength: -1L),
                index: 4L);
            var missingLength = CreateStateMachineEntry(
                new MissingLengthApplicationLogEntry(DeleteKeyValueCommand.Id, [0]),
                index: 5L);

            Assert.True(await InvokeApplyAsync(state, unknown));
            Assert.False(await InvokeApplyAsync(state, oversized));
            Assert.False(await InvokeApplyAsync(state, negativeLength));
            Assert.False(await InvokeApplyAsync(state, missingLength));

            Assert.Empty(state.SlimDataState.Queues);
            var skipped = state.GetSkippedCommandMetrics();
            Assert.Contains(skipped, metric => metric.CommandId == 999 && metric.Count == 1L);
            Assert.Contains(skipped, metric => metric.CommandId == ListCallbackCommand.Id && metric.Count == 1L);
            Assert.Contains(skipped, metric => metric.CommandId == ListCallbackBatchCommand.Id && metric.Count == 1L);
            Assert.Contains(skipped, metric => metric.CommandId == DeleteKeyValueCommand.Id && metric.Count == 1L);
            var safety = state.GetRaftSafetyMetrics();
            Assert.Equal(1L, safety.SizeViolations);
            Assert.Equal(3L, safety.FormatViolations);
            Assert.Equal(5L, safety.LastSkippedIndex);
            Assert.Equal(DeleteKeyValueCommand.Id, safety.LastSkippedCommandId);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAheadLog_replays_valid_command_corruption_then_valid_command()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);

            await AppendCommitWaitAsync(wal, new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "before-corruption",
                Value = Encoding.UTF8.GetBytes("before")
            });

            var index = await wal.AppendAsync(
                new InvalidApplicationLogEntry(ListRightPopCommand.Id, [0]),
                CancellationToken.None);
            await wal.CommitAsync(index, CancellationToken.None);
            using var applyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await wal.WaitForApplyAsync(index, applyTimeout.Token);

            await AppendCommitWaitAsync(wal, new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "after-corruption",
                Value = Encoding.UTF8.GetBytes("after")
            });

            Assert.Equal("before", Encoding.UTF8.GetString(state.SlimDataState.KeyValues["before-corruption"].Span));
            Assert.Equal("after", Encoding.UTF8.GetString(state.SlimDataState.KeyValues["after-corruption"].Span));
            Assert.Contains(
                state.GetSkippedCommandMetrics(),
                metric => metric.CommandId == ListRightPopCommand.Id && metric.Count == 1L);
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

    private static LogEntry CreateStateMachineEntry(
        IRaftLogEntry entry,
        long index,
        bool isConfiguration = false,
        object? context = null)
    {
        var constructor = typeof(LogEntry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IRaftLogEntry), typeof(long)],
            modifiers: null);

        Assert.NotNull(constructor);
        var stateMachineEntry = (LogEntry)constructor.Invoke([entry, index]);
        if (!isConfiguration && context is null)
            return stateMachineEntry;

        var boxedEntry = (object)stateMachineEntry;
        if (isConfiguration)
        {
            var configurationField = typeof(LogEntry).GetField(
                "<IsConfiguration>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(configurationField);
            configurationField.SetValue(boxedEntry, true);
        }

        if (context is not null)
        {
            var contextField = typeof(LogEntry).GetField(
                "<Context>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.NotNull(contextField);
            contextField.SetValue(boxedEntry, context);
        }

        return (LogEntry)boxedEntry;
    }

    private static async ValueTask<bool> InvokeApplyAsync(SlimPersistentState state, LogEntry entry)
    {
        var method = typeof(SlimPersistentState).GetMethod(
            "ApplyAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return await (ValueTask<bool>)method.Invoke(state, [entry, CancellationToken.None])!;
    }

    private static ValueTask<bool> ApplySetAsync(SlimPersistentState state, long index, string key)
    {
        var result = new KeyValueCommandResult();
        var command = new LogEntry<AddKeyValueCommand>
        {
            Term = 1L,
            Command = new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = key,
                Value = Encoding.UTF8.GetBytes("value")
            },
            Context = result
        };
        return InvokeApplyAsync(state, CreateStateMachineEntry(command, index, context: result));
    }

    private static async Task InvokeRestoreAsync(
        SlimPersistentState state,
        FileInfo snapshot,
        CancellationToken token)
    {
        var method = typeof(SlimPersistentState).GetMethod(
            "RestoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(FileInfo), typeof(CancellationToken)],
            modifiers: null);

        Assert.NotNull(method);
        await (ValueTask)method.Invoke(state, [snapshot, token])!;
    }

    private static SlimPersistentState CreateState(string root, int snapshotIntervalEntries)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SlimPersistentState.LogLocation] = root,
                [SlimPersistentState.SnapshotIntervalEntries] = snapshotIntervalEntries.ToString()
            })
            .Build();
        return new SlimPersistentState(configuration, logger: null);
    }

    private sealed class InvalidConfigurationLogEntry : IRaftLogEntry
    {
        private static readonly byte[] Payload = [0];

        public long Term => 1L;
        public int? CommandId => AddKeyValueCommand.Id;
        public bool IsConfiguration => true;
        public bool IsSnapshot => false;
        public bool IsReusable => true;
        public long? Length => Payload.Length;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter
            => writer.Invoke(Payload, token);
    }

    private sealed class LegacyAddKeyValueLogEntry(byte[] payload) : IRaftLogEntry
    {
        public long Term => 1L;
        public int? CommandId => AddKeyValueCommand.Id;
        public bool IsConfiguration => false;
        public bool IsSnapshot => false;
        public bool IsReusable => true;
        public long? Length => payload.Length;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter
            => writer.Invoke(payload, token);
    }

    private sealed class InvalidApplicationLogEntry(
        int commandId,
        byte[] payload,
        long? declaredLength = null) : IRaftLogEntry
    {
        public long Term => 1L;
        public int? CommandId => commandId;
        public bool IsConfiguration => false;
        public bool IsSnapshot => false;
        public bool IsReusable => true;
        public long? Length => declaredLength ?? payload.Length;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter
            => writer.Invoke(payload, token);
    }

    private sealed class MissingLengthApplicationLogEntry(int commandId, byte[] payload) : IRaftLogEntry
    {
        public long Term => 1L;
        public int? CommandId => commandId;
        public bool IsConfiguration => false;
        public bool IsSnapshot => false;
        public bool IsReusable => true;
        public long? Length => null;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter
            => writer.Invoke(payload, token);
    }

    private static string GetTemporaryDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }
}
