using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData.Tests;

public sealed class WalChunkSizeTests
{
    [Fact]
    public void Fresh_windows_wal_records_geometry_before_any_pages_are_written()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            Assert.Equal(65536, WalChunkSize.Resolve(root, isWindows: true));
            Assert.Equal("65536", File.ReadAllText(Path.Combine(root, WalChunkSize.MarkerFileName)));
            Assert.Equal(65536, WalChunkSize.Resolve(root, isWindows: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Existing_unmarked_wal_keeps_legacy_geometry()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(Environment.SystemPageSize, WalChunkSize.Resolve(root, isWindows: true));
            Assert.False(File.Exists(Path.Combine(root, WalChunkSize.MarkerFileName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Fresh_non_windows_wal_keeps_the_system_default_without_creating_files()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Assert.Equal(Environment.SystemPageSize, WalChunkSize.Resolve(root, isWindows: false));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-4096")]
    [InlineData("4097")]
    [InlineData("65536\n")]
    [InlineData("2147483648")]
    public void Invalid_marker_fails_instead_of_reinterpreting_persisted_addresses(string value)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        string marker = Path.Combine(root, WalChunkSize.MarkerFileName);
        File.WriteAllText(marker, value);
        try
        {
            Assert.Throws<InvalidDataException>(() => WalChunkSize.Resolve(root, isWindows: true));
            Assert.Equal(value, File.ReadAllText(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_entries_replay_with_their_original_chunk_geometry(bool legacy)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        string location = Path.Combine(root, "wal");
        if (legacy)
            Directory.CreateDirectory(location);
        byte[] value = Enumerable.Range(0, 180000).Select(static index => (byte)(index % 251)).ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            int originalSize = WalChunkSize.Resolve(location, isWindows: true);
            await using (SlimPersistentState state = new(root))
            {
                await state.RestoreAsync(deadline.Token);
                await using var wal = new DurableWriteAheadLog(new WriteAheadLog.Options
                {
                    Location = location, ChunkSize = originalSize
                }, state);
                IPersistentState raftLog = wal;
                await raftLog.AppendAsync(new LogEntry<AddKeyValueCommand>
                {
                    Term = 1,
                    Command = new AddKeyValueCommand { Key = "geometry", Value = value, Operation = KeyValueOperation.Set }
                }, deadline.Token);
                await raftLog.CommitAsync(1, deadline.Token);
                await wal.WaitForApplyAsync(1, deadline.Token);
            }

            int reopenedSize = WalChunkSize.Resolve(location, isWindows: !legacy);
            Assert.Equal(originalSize, reopenedSize);
            await using SlimPersistentState restored = new(root);
            await restored.RestoreAsync(deadline.Token);
            await using var reopened = new DurableWriteAheadLog(new WriteAheadLog.Options
            {
                Location = location, ChunkSize = reopenedSize
            }, restored);
            await reopened.InitializeAsync(deadline.Token);
            Assert.Equal(value, restored.SlimDataState.KeyValues["geometry"].ToArray());
            Assert.Equal(1, reopened.LastCommittedEntryIndex);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Unreadable_marker_does_not_fall_back_to_legacy_geometry()
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, WalChunkSize.MarkerFileName));
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => WalChunkSize.Resolve(root, isWindows: true));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
