using System.Text;
using System.Reflection;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using DotNext.Text;
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
    public async Task WriteAheadLog_applies_legacy_operation_first_key_value_command()
    {
        var root = GetTemporaryDirectory();
        var walPath = Path.Combine(root, "wal");

        try
        {
            await using var state = new SlimPersistentState(root);
            await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, state);

            var payload = await SerializeLegacyOperationFirstSetAsync(
                "legacy-wal-key",
                "legacy-wal-value");
            Assert.Equal((byte)KeyValueOperation.Set, payload[0]);

            var index = await wal.AppendAsync(
                new LegacyAddKeyValueLogEntry(payload),
                CancellationToken.None);
            await wal.CommitAsync(index, CancellationToken.None);
            await wal.WaitForApplyAsync(index, CancellationToken.None);

            Assert.Equal(
                "legacy-wal-value",
                Encoding.UTF8.GetString(state.SlimDataState.KeyValues["legacy-wal-key"].Span));
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

    private static LogEntry CreateStateMachineEntry(IRaftLogEntry entry, long index, bool isConfiguration = false)
    {
        var constructor = typeof(LogEntry).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(IRaftLogEntry), typeof(long)],
            modifiers: null);

        Assert.NotNull(constructor);
        var stateMachineEntry = (LogEntry)constructor.Invoke([entry, index]);
        if (!isConfiguration)
            return stateMachineEntry;

        var boxedEntry = (object)stateMachineEntry;
        var configurationField = typeof(LogEntry).GetField(
            "<IsConfiguration>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(configurationField);
        configurationField.SetValue(boxedEntry, true);
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

    private static async Task<byte[]> SerializeLegacyOperationFirstSetAsync(string key, string value)
    {
        await using var stream = new MemoryStream();
        var writer = IAsyncBinaryWriter.Create(stream, new byte[256]);

        await writer.WriteLittleEndianAsync((byte)KeyValueOperation.Set, CancellationToken.None);
        await writer.EncodeAsync(
                key.AsMemory(),
                new EncodingContext(Encoding.UTF8, false),
                LengthFormat.LittleEndian,
                CancellationToken.None)
            .ConfigureAwait(false);

        await writer.WriteLittleEndianAsync(DateTime.UtcNow.Ticks, CancellationToken.None);
        await writer.WriteLittleEndianAsync((byte)0, CancellationToken.None);
        await writer.WriteLittleEndianAsync(0L, CancellationToken.None);

        var decimalBits = decimal.GetBits(0m);
        foreach (var bit in decimalBits)
            await writer.WriteLittleEndianAsync(bit, CancellationToken.None);

        await writer.WriteAsync(
            Encoding.UTF8.GetBytes(value),
            LengthFormat.Compressed,
            CancellationToken.None);

        return stream.ToArray();
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

    private static string GetTemporaryDirectory()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        return tempDirectory;
    }
}
