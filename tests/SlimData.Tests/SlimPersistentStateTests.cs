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
    public async Task Restore_resets_snapshot_counters()
    {
        var root = GetTemporaryDirectory();
        var snapshotPath = Path.Combine(root, "100-1");

        try
        {
            // Three empty collection counts in the existing snapshot binary format.
            await File.WriteAllBytesAsync(snapshotPath, new byte[3 * sizeof(int)]);
            await using var state = CreateState(
                root,
                snapshotIntervalEntries: 2,
                snapshotIntervalBytes: long.MaxValue);

            Assert.False(await ApplySetAsync(state, index: 1L, key: "before-restore"));
            Assert.True(state.WalBytesSinceSnapshot > 0L);

            await InvokeRestoreAsync(state, new FileInfo(snapshotPath), CancellationToken.None);

            Assert.Equal(0L, state.WalBytesSinceSnapshot);
            Assert.Equal(SlimDataSnapshotTrigger.None, state.LastSnapshotTrigger);
            Assert.False(await ApplySetAsync(state, index: 101L, key: "first"));
            Assert.True(await ApplySetAsync(state, index: 200L, key: "second"));
            Assert.Equal(SlimDataSnapshotTrigger.Entries, state.LastSnapshotTrigger);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_uses_memory_safe_default_snapshot_thresholds()
    {
        Assert.Equal(500, SlimPersistentState.DefaultSnapshotIntervalEntries);
        Assert.Equal(32L * 1024L * 1024L, SlimPersistentState.DefaultSnapshotIntervalBytes);

        var root = GetTemporaryDirectory();

        try
        {
            await using var state = new SlimPersistentState(root);

            for (var index = 1L; index < SlimPersistentState.DefaultSnapshotIntervalEntries; index++)
                Assert.False(await ApplySetAsync(state, index, "default-entry-threshold"));

            Assert.True(await ApplySetAsync(
                state,
                SlimPersistentState.DefaultSnapshotIntervalEntries,
                "default-entry-threshold"));
            Assert.Equal(SlimDataSnapshotTrigger.Entries, state.LastSnapshotTrigger);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_requests_snapshot_at_five_thousand_applied_entries()
    {
        var root = GetTemporaryDirectory();

        try
        {
            await using var state = CreateState(
                root,
                snapshotIntervalEntries: 5000,
                snapshotIntervalBytes: SlimPersistentState.DefaultSnapshotIntervalBytes);

            for (var index = 1L; index < 5000L; index++)
                Assert.False(await ApplySetAsync(state, index, "entry-threshold"));

            Assert.InRange(
                state.WalBytesSinceSnapshot,
                1L,
                SlimPersistentState.DefaultSnapshotIntervalBytes - 1L);
            Assert.True(await ApplySetAsync(state, index: 5000L, key: "entry-threshold"));
            Assert.Equal(0L, state.WalBytesSinceSnapshot);
            Assert.Equal(SlimDataSnapshotTrigger.Entries, state.LastSnapshotTrigger);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApplyAsync_requests_snapshot_when_an_entry_crosses_the_byte_interval()
    {
        var root = GetTemporaryDirectory();
        var firstEntry = CreateSetEntry(index: 1L, key: "first");
        var secondEntry = CreateSetEntry(index: 2L, key: "second");
        var byteInterval = firstEntry.Length.GetValueOrDefault() + secondEntry.Length.GetValueOrDefault() - 1L;

        try
        {
            await using var state = CreateState(
                root,
                snapshotIntervalEntries: 5000,
                snapshotIntervalBytes: byteInterval);

            Assert.False(await InvokeApplyAsync(state, firstEntry));
            Assert.Equal(firstEntry.Length.GetValueOrDefault(), state.WalBytesSinceSnapshot);

            Assert.True(await InvokeApplyAsync(state, secondEntry));
            Assert.Equal(0L, state.WalBytesSinceSnapshot);
            Assert.Equal(SlimDataSnapshotTrigger.Bytes, state.LastSnapshotTrigger);

            var thirdEntry = CreateSetEntry(index: 3L, key: "third");
            Assert.False(await InvokeApplyAsync(state, thirdEntry));
            Assert.Equal(thirdEntry.Length.GetValueOrDefault(), state.WalBytesSinceSnapshot);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Configuration_reads_json_snapshot_intervals()
    {
        var root = GetTemporaryDirectory();

        try
        {
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "SlimData": {
                    "SnapshotIntervalEntries": 2,
                    "SnapshotIntervalBytes": 67108864
                  }
                }
                """));
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(json)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SlimPersistentState.LogLocation] = root
                })
                .Build();

            await using var state = new SlimPersistentState(configuration, logger: null);
            Assert.False(await ApplySetAsync(state, index: 1L, key: "first"));
            Assert.True(await ApplySetAsync(state, index: 2L, key: "second"));
            Assert.Equal(SlimDataSnapshotTrigger.Entries, state.LastSnapshotTrigger);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Configuration_reads_json_settings_and_environment_overrides()
    {
        var root = GetTemporaryDirectory();
        var prefix = $"SLIMDATA_TEST_{Guid.NewGuid():N}_";
        var entriesVariable = $"{prefix}SlimData__SnapshotIntervalEntries";
        var bytesVariable = $"{prefix}SlimData__SnapshotIntervalBytes";

        try
        {
            Environment.SetEnvironmentVariable(entriesVariable, "2");
            Environment.SetEnvironmentVariable(bytesVariable, long.MaxValue.ToString());
            using var json = new MemoryStream(Encoding.UTF8.GetBytes(
                """
                {
                  "SlimData": {
                    "SnapshotIntervalEntries": 100,
                    "SnapshotIntervalBytes": 67108864
                  }
                }
                """));
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(json)
                .AddEnvironmentVariables(prefix)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SlimPersistentState.LogLocation] = root
                })
                .Build();

            await using var state = new SlimPersistentState(configuration, logger: null);
            Assert.False(await ApplySetAsync(state, index: 1L, key: "first"));
            Assert.True(await ApplySetAsync(state, index: 2L, key: "second"));
            Assert.Equal(SlimDataSnapshotTrigger.Entries, state.LastSnapshotTrigger);
        }
        finally
        {
            Environment.SetEnvironmentVariable(entriesVariable, null);
            Environment.SetEnvironmentVariable(bytesVariable, null);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 67108864L, "snapshotIntervalEntries")]
    [InlineData(-1, 67108864L, "snapshotIntervalEntries")]
    [InlineData(5000, 0L, "snapshotIntervalBytes")]
    [InlineData(5000, -1L, "snapshotIntervalBytes")]
    public void Configuration_rejects_non_positive_snapshot_intervals(
        int snapshotIntervalEntries,
        long snapshotIntervalBytes,
        string expectedParameter)
    {
        var root = GetTemporaryDirectory();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SlimPersistentState.LogLocation] = root,
                    [SlimPersistentState.SnapshotIntervalEntries] = snapshotIntervalEntries.ToString(),
                    [SlimPersistentState.SnapshotIntervalBytes] = snapshotIntervalBytes.ToString()
                })
                .Build();

            var exception = Assert.Throws<ArgumentOutOfRangeException>(
                () => new SlimPersistentState(configuration, logger: null));

            Assert.Equal(expectedParameter, exception.ParamName);
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
            Assert.Equal(SlimDataSnapshotTrigger.Incompatible, state.LastSnapshotTrigger);
            Assert.Equal(0L, state.WalBytesSinceSnapshot);
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

    [Fact]
    public async Task WriteAheadLog_applies_pre_serialized_key_value_entry_without_skipping_it()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);
            var result = new KeyValueCommandResult();
            var context = new KeyValueCommandBatchContext([result]);
            var command = new AddKeyValueCommand
            {
                Items =
                [
                    new AddKeyValueCommand.BatchItem
                    {
                        Operation = KeyValueOperation.Set,
                        Key = "history-key",
                        Value = Encoding.UTF8.GetBytes("history-value"),
                        NowTicks = DateTime.UtcNow.Ticks
                    }
                ]
            };
            var payload = command.Serialize();
            var entry = SerializedSlimDataLogEntry.Create(
                AddKeyValueCommand.Id,
                1L,
                payload,
                context,
                nameof(AddKeyValueCommand));

            var index = await wal.AppendAsync(entry, CancellationToken.None);
            await wal.CommitAsync(index, CancellationToken.None);
            using var applyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await wal.WaitForApplyAsync(index, applyTimeout.Token);

            Assert.Equal(KeyValueCommandStatus.Applied, result.Status);
            Assert.Equal(
                "history-value",
                Encoding.UTF8.GetString(state.SlimDataState.KeyValues["history-key"].Span));
            Assert.DoesNotContain(
                state.GetSkippedCommandMetrics(),
                metric => metric.CommandId == AddKeyValueCommand.Id);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAheadLog_recovers_zero_prefixed_current_commands()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);

            var keyValuePayload = PrefixWithZeros(new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "recovered-key",
                Value = Encoding.UTF8.GetBytes("recovered-value"),
                NowTicks = DateTime.UtcNow.Ticks
            }.Serialize());
            await AppendBinaryCommitWaitAsync(wal, AddKeyValueCommand.Id, keyValuePayload);

            var queueCommand = new ListLeftPushBatchCommand
            {
                Items =
                [
                    new ListLeftPushBatchCommand.BatchItem
                    {
                        Key = "recovered-queue",
                        Identifier = "recovered-item",
                        NowTicks = DateTime.UtcNow.Ticks,
                        RetryTimeout = 30,
                        Retries = [],
                        HttpStatusCodesWorthRetrying = [],
                        Value = Encoding.UTF8.GetBytes("queue-value")
                    }
                ]
            };
            var queuePayload = await DataTransferObject.ToByteArrayAsync(
                queueCommand,
                null,
                CancellationToken.None);
            await AppendBinaryCommitWaitAsync(
                wal,
                ListLeftPushBatchCommand.Id,
                PrefixWithZeros(queuePayload));

            Assert.Equal(
                "recovered-value",
                Encoding.UTF8.GetString(state.SlimDataState.KeyValues["recovered-key"].Span));
            Assert.Equal(
                "recovered-item",
                Assert.Single(state.SlimDataState.Queues["recovered-queue"]).Id);
            Assert.DoesNotContain(
                state.GetSkippedCommandMetrics(),
                metric => metric.CommandId is AddKeyValueCommand.Id or ListLeftPushBatchCommand.Id);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAheadLog_recovers_page_alignment_padding_for_streamed_entries()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);
            var pageSize = Environment.SystemPageSize;
            var firstPayload = new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "page-first",
                Value = new byte[pageSize - 2500],
                NowTicks = DateTime.UtcNow.Ticks
            }.Serialize();
            var secondPayload = new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "page-second",
                Value = new byte[3000],
                NowTicks = DateTime.UtcNow.Ticks
            }.Serialize();
            var remainingPageBytes = pageSize - firstPayload.Length;

            Assert.InRange(firstPayload.Length, 1, pageSize);
            Assert.InRange(secondPayload.Length, remainingPageBytes + 1, pageSize);

            await AppendStreamingCommitWaitAsync(wal, AddKeyValueCommand.Id, firstPayload);
            await AppendStreamingCommitWaitAsync(wal, AddKeyValueCommand.Id, secondPayload);

            Assert.True(state.SlimDataState.KeyValues.ContainsKey("page-first"));
            Assert.True(state.SlimDataState.KeyValues.ContainsKey("page-second"));
            Assert.DoesNotContain(
                state.GetSkippedCommandMetrics(),
                metric => metric.CommandId == AddKeyValueCommand.Id);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAheadLog_applies_every_pre_serialized_command_type_without_skipping()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);
            var now = DateTime.UtcNow.Ticks;

            await AppendSerializedCommitWaitAsync(wal, new AddHashSetCommand
            {
                Key = "all-hash",
                Value = new Dictionary<string, ReadOnlyMemory<byte>>
                {
                    ["field"] = Encoding.UTF8.GetBytes("value")
                }
            });
            await AppendSerializedCommitWaitAsync(wal, new AddKeyValueCommand
            {
                Operation = KeyValueOperation.Set,
                Key = "all-key",
                Value = Encoding.UTF8.GetBytes("value"),
                NowTicks = now
            });
            await AppendSerializedCommitWaitAsync(wal, new ListLeftPushBatchCommand
            {
                Items =
                [
                    new ListLeftPushBatchCommand.BatchItem
                    {
                        Key = "all-queue",
                        Identifier = "item",
                        NowTicks = now,
                        RetryTimeout = 30,
                        Retries = [],
                        HttpStatusCodesWorthRetrying = [],
                        Value = Encoding.UTF8.GetBytes("value")
                    }
                ]
            });
            await AppendSerializedCommitWaitAsync(wal, new ListRightPopCommand
            {
                Key = "all-queue",
                Count = 1,
                NowTicks = now + 1,
                IdTransaction = "tx",
                ReservedIps = []
            });
            await AppendSerializedCommitWaitAsync(wal, new ListCallbackCommand
            {
                Key = "all-queue",
                NowTicks = now + 2,
                CallbackElements = [new CallbackElement("item", 200)]
            });
            await AppendSerializedCommitWaitAsync(wal, new ListCallbackBatchCommand
            {
                Items =
                [
                    new ListCallbackBatchCommand.BatchItem
                    {
                        Key = "all-queue",
                        NowTicks = now + 3,
                        CallbackElements = [new CallbackElement("missing-item", 200)]
                    }
                ]
            });
            await AppendSerializedCommitWaitAsync(wal, new DeleteHashSetCommand
            {
                Key = "all-hash",
                DictionaryKey = "field"
            });
            await AppendSerializedCommitWaitAsync(wal, new DeleteKeyValueCommand { Key = "all-key" });

            Assert.Empty(state.GetSkippedCommandMetrics());
            Assert.False(state.SlimDataState.KeyValues.ContainsKey("all-key"));
            Assert.False(state.SlimDataState.Hashsets.TryGetValue("all-hash", out var hash) &&
                         hash.ContainsKey("field"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAheadLog_skips_legacy_list_left_push_then_applies_current_entry()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            var legacyCommand = new ListLeftPushBatchCommand
            {
                Items =
                [
                    new ListLeftPushBatchCommand.BatchItem
                    {
                        Key = "legacy-queue",
                        Identifier = "legacy-item",
                        NowTicks = DateTime.UtcNow.Ticks,
                        RetryTimeout = 30,
                        Retries = [1, 2],
                        HttpStatusCodesWorthRetrying = [500],
                        Value = Encoding.UTF8.GetBytes("legacy-value")
                    }
                ]
            };
            var payload = await SlimDataCommandCodecTests.SerializeLegacyListLeftPushAsync(legacyCommand);

            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);
            var index = await wal.AppendAsync(
                new InvalidApplicationLogEntry(ListLeftPushBatchCommand.Id, payload),
                CancellationToken.None);
            await wal.CommitAsync(index, CancellationToken.None);
            using var applyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await wal.WaitForApplyAsync(index, applyTimeout.Token);

            Assert.DoesNotContain("legacy-queue", state.SlimDataState.Queues.Keys);
            Assert.Contains(
                state.GetSkippedCommandMetrics(),
                metric => metric.CommandId == ListLeftPushBatchCommand.Id && metric.Count == 1L);

            await AppendCommitWaitAsync(wal, new ListLeftPushBatchCommand
            {
                Items =
                [
                    new ListLeftPushBatchCommand.BatchItem
                    {
                        Key = "current-queue",
                        Identifier = "current-item",
                        NowTicks = DateTime.UtcNow.Ticks,
                        RetryTimeout = 30,
                        Retries = [1, 2],
                        HttpStatusCodesWorthRetrying = [500],
                        Value = Encoding.UTF8.GetBytes("current-value")
                    }
                ]
            });

            var item = Assert.Single(state.SlimDataState.Queues["current-queue"]);
            Assert.Equal("current-item", item.Id);
            Assert.Equal("current-value", Encoding.UTF8.GetString(item.Value.Span));
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

    private static async Task AppendSerializedCommitWaitAsync<TCommand>(WriteAheadLog wal, TCommand command)
        where TCommand : struct, ICommand<TCommand>
    {
        var entry = await SerializedSlimDataLogEntry.CreateAsync(
            command,
            term: 1L,
            new CommandApplyContext(),
            CancellationToken.None);
        var index = await wal.AppendAsync(entry, CancellationToken.None);
        await wal.CommitAsync(index, CancellationToken.None);
        await wal.WaitForApplyAsync(index, CancellationToken.None);
    }

    private static async Task AppendBinaryCommitWaitAsync(
        WriteAheadLog wal,
        int commandId,
        byte[] payload)
    {
        var entry = new BinaryLogEntry
        {
            CommandId = commandId,
            Term = 1L,
            Content = payload,
            Context = new CommandApplyContext()
        };
        var index = await wal.AppendAsync(entry, CancellationToken.None);
        await wal.CommitAsync(index, CancellationToken.None);
        await wal.WaitForApplyAsync(index, CancellationToken.None);
    }

    private static async Task AppendStreamingCommitWaitAsync(
        WriteAheadLog wal,
        int commandId,
        byte[] payload)
    {
        var index = await wal.AppendAsync(
            new InvalidApplicationLogEntry(commandId, payload),
            CancellationToken.None);
        await wal.CommitAsync(index, CancellationToken.None);
        await wal.WaitForApplyAsync(index, CancellationToken.None);
    }

    private static byte[] PrefixWithZeros(byte[] payload)
    {
        var result = new byte[payload.Length + sizeof(int)];
        payload.CopyTo(result, sizeof(int));
        return result;
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
        => InvokeApplyAsync(state, CreateSetEntry(index, key));

    private static LogEntry CreateSetEntry(long index, string key)
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
        return CreateStateMachineEntry(command, index, context: result);
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

    private static SlimPersistentState CreateState(
        string root,
        int snapshotIntervalEntries,
        long snapshotIntervalBytes = SlimPersistentState.DefaultSnapshotIntervalBytes)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SlimPersistentState.LogLocation] = root,
                [SlimPersistentState.SnapshotIntervalEntries] = snapshotIntervalEntries.ToString(),
                [SlimPersistentState.SnapshotIntervalBytes] = snapshotIntervalBytes.ToString()
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
