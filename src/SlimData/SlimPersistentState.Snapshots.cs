// Snapshot lifecycle adapted from DotNext SimpleStateMachine, commit
// 0c1a6621a397494d8a6f633d8a5176752b5ec9e5, under the MIT license.
// Copyright (c) .NET Foundation and Contributors. See ThirdPartyNotices/DotNext.txt.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData;

public sealed partial class SlimPersistentState
{
    private const int SnapshotTransferBufferSize = 64 * 1024;
    private readonly DirectoryInfo _snapshotDirectory;
    private readonly CancellationToken _snapshotLifetimeToken;
    private readonly Task<SnapshotPersistence> _snapshotSentinel;
    [SuppressMessage("Usage", "CA2213", Justification = "Ownership is transferred with Interlocked.Exchange to DisposeSnapshotsAsync.")]
    private CancellationTokenSource? _snapshotLifetimeSource = new();
    private long _snapshotAppliedIndex;
    private volatile StateSnapshot? _committedSnapshot;
    private volatile Task<SnapshotPersistence>? _snapshottingProcess;

    private IEnumerable<StateSnapshot> EnumerateSnapshots()
        => _snapshotDirectory.EnumerateFiles("*-*", SearchOption.TopDirectoryOnly)
            .Select(static file => new StateSnapshot(file));

    /// <summary>Restores the newest complete snapshot before the WAL is opened.</summary>
    public async ValueTask RestoreAsync(CancellationToken token = default)
    {
        bool foundSnapshot = false;
        foreach (StateSnapshot candidate in EnumerateSnapshots().OrderByDescending(static value => value.Index))
        {
            token.ThrowIfCancellationRequested();
            foundSnapshot = true;
            try
            {
                await RestoreAsync(candidate.File, token).ConfigureAwait(false);
            }
            catch (EndOfStreamException exception)
            {
                // Older DotNext receivers promoted interrupted copies in finally.
                // Retain every rejected file; never start with empty state merely
                // because the newest on-disk snapshot is truncated.
                _logger?.LogTruncatedSnapshotRetained(exception, candidate.File.FullName, candidate.Index);
                continue;
            }
            _committedSnapshot = candidate;
            _snapshotAppliedIndex = candidate.Index;
            return;
        }
        if (foundSnapshot)
            throw new InvalidDataException("No complete SlimData snapshot is available; retained files require recovery.");
        _snapshotAppliedIndex = 0L;
    }

    ISnapshot? ISnapshotManager.Snapshot => _snapshotAppliedIndex >= 0L
        ? _committedSnapshot
        : throw new InvalidOperationException("The SlimData state machine has not been restored.");

    ValueTask ISnapshotManager.ReclaimGarbageAsync(long watermark, CancellationToken token)
    {
        foreach (StateSnapshot candidate in EnumerateSnapshots())
        {
            token.ThrowIfCancellationRequested();
            if (candidate.Index < watermark)
                candidate.File.Delete();
        }
        return ValueTask.CompletedTask;
    }

    ValueTask<long> IStateMachine.ApplyAsync(LogEntry entry, CancellationToken token)
        => _snapshotAppliedIndex >= entry.Index
            ? ValueTask.FromResult(_snapshotAppliedIndex)
            : entry.IsSnapshot ? InstallReceivedSnapshotAsync(entry, token) : ApplyAndSnapshotAsync(entry, token);

    private async ValueTask<long> ApplyAndSnapshotAsync(LogEntry entry, CancellationToken token)
    {
        await EndSnapshottingAsync(commit: true).ConfigureAwait(false);
        if (await ApplyAsync(entry, token).ConfigureAwait(false))
        {
            Task<SnapshotPersistence> task = Task.Run(
                () => CreateSnapshotAsync(entry.Index, entry.Term, _snapshotLifetimeToken));
            if (_snapshottingProcess is not null ||
                Interlocked.CompareExchange(ref _snapshottingProcess, task, null) is not null)
                await task.ConfigureAwait(false);
        }
        return _snapshotAppliedIndex = entry.Index;
    }

    private async Task EndSnapshottingAsync(bool commit)
    {
        if (_snapshottingProcess is not { } task)
            return;
        SnapshotPersistence writer = await task.ConfigureAwait(false);
        writer.Dispose();
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _snapshottingProcess, null, task), task))
            return;
        if (commit)
        {
            writer.Commit();
            _committedSnapshot = new StateSnapshot(writer.Destination);
        }
        else
        {
            writer.Rollback();
        }
    }

    private async Task<SnapshotPersistence> CreateSnapshotAsync(long index, long term, CancellationToken token)
    {
        SnapshotPersistence writer = new(SnapshotFile(index, term), 0L);
        try
        {
            await PersistAsync(writer.Writer, token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
            return writer;
        }
        catch
        {
            try { writer.Dispose(); }
            finally { writer.Rollback(); }
            throw;
        }
    }

    private FileInfo SnapshotFile(long index, long term)
        => new(Path.Combine(_snapshotDirectory.FullName, FormattableString.Invariant($"{index}-{term}")));

    private async ValueTask<long> InstallReceivedSnapshotAsync(LogEntry entry, CancellationToken token)
    {
        await EndSnapshottingAsync(commit: false).ConfigureAwait(false);
        SnapshotPersistence writer = new(SnapshotFile(entry.Index, entry.Term), entry.Length.GetValueOrDefault());
        try
        {
            await entry.WriteToAsync(writer.Writer, token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
            if (entry.Length is { } length && writer.Length != length)
                throw new EndOfStreamException("The received snapshot does not match its declared length.");
            token.ThrowIfCancellationRequested();
            writer.Dispose();
            // Decode into detached state before publishing either the file or
            // the in-memory payload. A complete transport is not proof that its
            // body is a valid application snapshot.
            SlimDataStateSnapshot restored = await ReadSnapshotAsync(writer.TemporaryFile, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Only a complete, flushed copy becomes visible under index-term.
            writer.Commit();
            PublishRestoredSnapshot(restored, writer.Destination.Length);
        }
        catch
        {
            try { writer.Dispose(); }
            finally { writer.Rollback(); }
            throw;
        }
        _committedSnapshot = new StateSnapshot(writer.Destination);
        return _snapshotAppliedIndex = entry.Index;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return Interlocked.Exchange(ref _snapshotLifetimeSource, null) is { } source
            ? DisposeSnapshotsAsync(source)
            : ValueTask.CompletedTask;
    }

    private async ValueTask DisposeSnapshotsAsync(CancellationTokenSource source)
    {
        using (source)
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        Task<SnapshotPersistence>? task = Interlocked.Exchange(ref _snapshottingProcess, _snapshotSentinel);
        if (task is not null && !ReferenceEquals(task, _snapshotSentinel))
        {
            await ((Task)task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (task.IsFaulted && _logger?.IsEnabled(LogLevel.Error) == true)
                _logger.LogSnapshotCreationFailedDuringShutdown(task.Exception!);
            if (task.IsCompletedSuccessfully)
            {
                SnapshotPersistence writer = await task.ConfigureAwait(false);
                try { writer.Dispose(); }
                finally { writer.Rollback(); }
            }
        }
    }

    private sealed class SnapshotPersistence : IDisposable
    {
        private readonly string _temporaryPath;
        private readonly FileStream _stream;
        internal FileInfo Destination { get; }
        internal FileInfo TemporaryFile => new(_temporaryPath);
        internal IAsyncBinaryWriter Writer { get; }
        internal long Length => _stream.Position;

        internal SnapshotPersistence(FileInfo destination, long preallocationSize)
        {
            Destination = destination;
            // No '-' in temporary names: startup enumerates only index-term.
            _temporaryPath = Path.Combine(destination.DirectoryName!, Guid.NewGuid().ToString("N") + ".tmp");
            _stream = new FileStream(_temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                BufferSize = SnapshotTransferBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                PreallocationSize = preallocationSize
            });
            Writer = IAsyncBinaryWriter.Create(_stream, new byte[SnapshotTransferBufferSize]);
        }

        internal async Task FlushAsync(CancellationToken token)
        {
            await _stream.FlushAsync(token).ConfigureAwait(false);
            _stream.Flush(flushToDisk: true);
        }
        internal void Commit()
        {
            File.Move(_temporaryPath, Destination.FullName, overwrite: true);
            Destination.Refresh();
        }
        internal void Rollback() => File.Delete(_temporaryPath);
        public void Dispose() => _stream.Dispose();
    }

    private sealed class StateSnapshot : ISnapshot
    {
        internal FileInfo File { get; }
        public long Index { get; }
        public long Term { get; }
        long? IDataTransferObject.Length => File.Length;
        bool IDataTransferObject.IsReusable => true;

        internal StateSnapshot(FileInfo file)
        {
            string[] parts = file.Name.Split('-');
            if (parts.Length != 2)
                throw new InvalidDataException("Invalid SlimData snapshot filename.");
            Index = long.Parse(parts[0], CultureInfo.InvariantCulture);
            Term = long.Parse(parts[1], CultureInfo.InvariantCulture);
            File = file;
        }

        public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter
        {
            await using FileStream stream = File.Open(new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
                BufferSize = SnapshotTransferBufferSize,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            byte[] buffer = ArrayPool<byte>.Shared.Rent(SnapshotTransferBufferSize);
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                    await writer.Invoke(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
