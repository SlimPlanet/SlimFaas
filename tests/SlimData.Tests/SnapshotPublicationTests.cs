using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using SlimData.Commands;
using System.Text;

namespace SlimData.Tests;

public sealed class SnapshotPublicationTests
{
    [Fact]
    public async Task Startup_retains_a_truncated_newer_snapshot_and_restores_the_previous_complete_one()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        System.IO.Compression.ZipFile.ExtractToDirectory(
            Path.Combine(AppContext.BaseDirectory, "Snapshots", "dotnext-6.6.0-wal.zip"), root);
        string incomplete = Path.Combine(root, "db", "1000-1");
        await File.WriteAllBytesAsync(incomplete, new byte[4]);
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync();
            Assert.Equal(151, ((ISnapshotManager)state).Snapshot!.Index);
            Assert.Equal("value-for-existing-0", Encoding.UTF8.GetString(state.SlimDataState.KeyValues["data:set:existing-0"].Span));
            Assert.Equal(new byte[4], await File.ReadAllBytesAsync(incomplete));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Invalid_incoming_snapshot_preserves_previously_applied_values()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync();
            await using WriteAheadLog wal = new(new WriteAheadLog.Options { Location = Path.Combine(root, "wal") }, state);
            await wal.InitializeAsync();
            await SetAsync(wal, "confirmed", "retained"u8.ToArray());
            Exception? failure = await Record.ExceptionAsync(async () =>
                await wal.AppendAsync(new SnapshotBytes(new byte[4], 4), 10, CancellationToken.None));
            Assert.NotNull(failure);
            Assert.Equal("retained"u8.ToArray(), state.SlimDataState.KeyValues["confirmed"].ToArray());
            Assert.False(File.Exists(Path.Combine(root, "db", "10-1")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Snapshot_transfer_keeps_the_exact_persisted_bytes_across_buffer_boundaries()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            await using SlimPersistentState state = new(root, snapshotIntervalEntries: 1);
            await state.RestoreAsync();
            await using WriteAheadLog wal = new(new WriteAheadLog.Options { Location = Path.Combine(root, "wal") }, state);
            await wal.InitializeAsync();
            byte[] body = Enumerable.Range(0, 131079).Select(static value => (byte)value).ToArray();
            await SetAsync(wal, "body", body);
            await SetAsync(wal, "flush-previous-snapshot", [1]);
            ISnapshot snapshot = ((ISnapshotManager)state).Snapshot!;
            Assert.NotNull(snapshot);
            byte[] expected = await File.ReadAllBytesAsync(Path.Combine(root, "db", $"{snapshot.Index}-{snapshot.Term}"));
            using MemoryStream copy = new();
            await snapshot.WriteToAsync(IAsyncBinaryWriter.Create(copy, new byte[4096]), CancellationToken.None);
            Assert.True(expected.Length > 131072);
            Assert.Equal(expected, copy.ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task SetAsync(WriteAheadLog wal, string key, byte[] value)
    {
        long index = await wal.AppendAsync(new LogEntry<AddKeyValueCommand>
        {
            Term = 1,
            Command = new AddKeyValueCommand { Operation = KeyValueOperation.Set, Key = key, Value = value }
        }, CancellationToken.None);
        await wal.CommitAsync(index, CancellationToken.None);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        await wal.WaitForApplyAsync(index, deadline.Token);
    }

    [Fact]
    public async Task Startup_with_no_complete_snapshot_fails_without_starting_an_empty_state()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "db"));
        string incomplete = Path.Combine(root, "db", "10-1");
        await File.WriteAllBytesAsync(incomplete, new byte[4]);
        try
        {
            await using SlimPersistentState state = new(root);
            await Assert.ThrowsAsync<InvalidDataException>(() => state.RestoreAsync().AsTask());
            Assert.Throws<InvalidOperationException>(() => ((ISnapshotManager)state).Snapshot);
            Assert.Equal(new byte[4], await File.ReadAllBytesAsync(incomplete));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(4, 12, false)]
    [InlineData(4, 4, false)]
    [InlineData(12, 12, true)]
    public async Task Only_a_complete_successful_snapshot_is_published(int bytes, long declaredLength, bool publish)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync();
            await using WriteAheadLog wal = new(new WriteAheadLog.Options { Location = Path.Combine(root, "wal") }, state);
            await wal.InitializeAsync();
            Exception? failure = await Record.ExceptionAsync(async () =>
                await wal.AppendAsync(new SnapshotBytes(new byte[bytes], declaredLength), 10, CancellationToken.None));
            Assert.Equal(publish, failure is null);
            string destination = Path.Combine(root, "db", "10-1");
            Assert.Equal(publish, File.Exists(destination));
            if (publish)
                Assert.Equal(new byte[12], await File.ReadAllBytesAsync(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_snapshot_does_not_publish_a_truncated_final_file(bool cancel)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "db"));
        string previous = Path.Combine(root, "db", "1-1");
        byte[] emptySnapshot = new byte[12];
        await File.WriteAllBytesAsync(previous, emptySnapshot);
        try
        {
            await using SlimPersistentState state = new(root);
            await state.RestoreAsync();
            await using WriteAheadLog wal = new(new WriteAheadLog.Options { Location = Path.Combine(root, "wal") }, state);
            await wal.InitializeAsync();
            using CancellationTokenSource cancellation = new();
            Exception? failure = await Record.ExceptionAsync(async () =>
                await wal.AppendAsync(new InterruptedSnapshot(cancel, cancellation), 10, cancellation.Token));

            Assert.NotNull(failure);
            Assert.False(File.Exists(Path.Combine(root, "db", "10-1")),
                "A failed snapshot transfer must not publish an index/term file that startup could select.");
            Assert.Equal(emptySnapshot, await File.ReadAllBytesAsync(previous));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InterruptedSnapshot(bool cancel, CancellationTokenSource cancellation) : IRaftLogEntry
    {
        private static readonly byte[] Prefix = new byte[4];
        public long Term => 1;
        public int? CommandId => null;
        public bool IsConfiguration => false;
        public bool IsSnapshot => true;
        public bool IsReusable => false;
        public long? Length => 12;

        public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter
        {
            await writer.Invoke(Prefix, token);
            if (cancel)
            {
                await cancellation.CancelAsync();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected snapshot-copy failure after a prefix.");
        }
    }

    private sealed class SnapshotBytes(byte[] bytes, long declaredLength) : IRaftLogEntry
    {
        public long Term => 1;
        public int? CommandId => null;
        public bool IsConfiguration => false;
        public bool IsSnapshot => true;
        public bool IsReusable => true;
        public long? Length => declaredLength;
        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : notnull, IAsyncBinaryWriter => writer.Invoke(bytes, token);
    }
}
