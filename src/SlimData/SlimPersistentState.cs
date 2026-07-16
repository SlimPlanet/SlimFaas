using System.Collections.Immutable;
using System.Diagnostics;
using DotNext;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging;
using SlimData.Commands;

namespace SlimData;

public readonly record struct SlimDataStateMetrics(
    int KeyValues,
    int Hashsets,
    int HashsetItems,
    int Queues,
    int QueueItems,
    long PayloadBytes);

public sealed class SlimPersistentState : SimpleStateMachine, ISupplier<SlimDataPayload>
{
    public const string LogLocation = "SlimData:LogLocation";
    public const string UsePersistentConfigurationStorage = "SlimData:UsePersistentConfigurationStorage";
    public const string SnapshotIntervalEntries = "SlimData:SnapshotIntervalEntries";
    public const int DefaultSnapshotIntervalEntries = 5000;
    private const string SkippedIncompatibleKeyValueMessage =
        "Skipped incompatible key/value log entry.";

    private readonly SlimDataState _state = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
    private readonly ILogger<SlimPersistentState>? _logger;
    private readonly int _snapshotIntervalEntries;
    private long _lastSnapshotRequestIndex;
    private int _legacyCompactionRequested;
    private int _isRestoring;
    private int _isSnapshotting;
    private long _lastSnapshotDurationMilliseconds;
    private long _lastSnapshotSizeBytes;

    public SlimPersistentState(string location)
        : this(location, DefaultSnapshotIntervalEntries, null)
    {
    }

    public SlimPersistentState(string location, ILogger<SlimPersistentState>? logger)
        : this(location, DefaultSnapshotIntervalEntries, logger)
    {
    }

    internal SlimPersistentState(string location, int snapshotIntervalEntries, ILogger<SlimPersistentState>? logger = null)
        : this(
            new DirectoryInfo(Path.GetFullPath(Path.Combine(NormalizeLocation(location), "db"))),
            snapshotIntervalEntries,
            logger)
    {
    }

    public SlimPersistentState(IConfiguration configuration)
        : this(configuration, null)
    {
    }

    public SlimPersistentState(IConfiguration configuration, ILogger<SlimPersistentState>? logger)
        : this(
            configuration[LogLocation] ?? string.Empty,
            configuration.GetValue(SnapshotIntervalEntries, DefaultSnapshotIntervalEntries),
            logger)
    {
    }

    private SlimPersistentState(
        DirectoryInfo location,
        int snapshotIntervalEntries,
        ILogger<SlimPersistentState>? logger)
        : base(location)
    {
        _snapshotIntervalEntries = Math.Max(1, snapshotIntervalEntries);
        _logger = logger;
        Interpreter = SlimDataInterpreter.InitInterpreter(_state);
    }

    private static string NormalizeLocation(string? location)
        => string.IsNullOrWhiteSpace(location)
            ? Path.Combine(Path.GetTempPath(), "SlimData", Guid.NewGuid().ToString("N"))
            : location;

    public CommandInterpreter Interpreter { get; }

    public SlimDataState SlimDataState => _state;

    public bool IsRestoring => Volatile.Read(ref _isRestoring) is 1;

    public bool IsSnapshotting => Volatile.Read(ref _isSnapshotting) is 1;

    public long LastSnapshotDurationMilliseconds => Volatile.Read(ref _lastSnapshotDurationMilliseconds);

    public long LastSnapshotSizeBytes => Volatile.Read(ref _lastSnapshotSizeBytes);

    internal SlimDataStateSnapshot CaptureSnapshot() => _state.Capture();

    public SlimDataStateMetrics GetStateMetrics()
    {
        var snapshot = _state.Capture();
        return new(
            snapshot.KeyValues.Count,
            snapshot.Hashsets.Count,
            snapshot.Hashsets.Values.Sum(static hashset => hashset.Count),
            snapshot.Queues.Count,
            snapshot.Queues.Values.Sum(static queue => queue.Length),
            snapshot.PayloadBytes);
    }

    SlimDataPayload ISupplier<SlimDataPayload>.Invoke()
        => _state.CapturePayload();

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.Length == 0L || entry.IsConfiguration || entry.IsSnapshot)
            return false;

        try
        {
            await Interpreter.InterpretAsync(entry, entry.Context, token).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            if (TrySkipIncompatibleKeyValueEntry(entry, ex))
                return ShouldCreateSnapshot(entry.Index, requestLegacyCompaction: true);

            throw new InvalidDataException(
                $"Failed to interpret SlimData Raft log entry. Index={entry.Index}, Term={entry.Term}, CommandId={entry.CommandId?.ToString() ?? "null"}, IsConfiguration={entry.IsConfiguration}, IsSnapshot={entry.IsSnapshot}, Length={entry.Length?.ToString() ?? "null"}.",
                ex);
        }

        if (entry.Context is CommandApplyContext applyContext)
            applyContext.SetApplied();

        return ShouldCreateSnapshot(entry.Index);
    }

    private bool ShouldCreateSnapshot(long index, bool requestLegacyCompaction = false)
    {
        if (requestLegacyCompaction && Interlocked.CompareExchange(ref _legacyCompactionRequested, 1, 0) is 0)
        {
            Volatile.Write(ref _lastSnapshotRequestIndex, index);
            return true;
        }

        var previous = Volatile.Read(ref _lastSnapshotRequestIndex);
        if (index - previous < _snapshotIntervalEntries)
            return false;

        Volatile.Write(ref _lastSnapshotRequestIndex, index);
        return true;
    }

    private bool TrySkipIncompatibleKeyValueEntry(LogEntry entry, InvalidDataException exception)
    {
        if (entry.CommandId != AddKeyValueCommand.Id)
            return false;

        _logger?.LogWarning(
            exception,
            "Skipping incompatible SlimData key/value Raft log entry. Index={Index}, Term={Term}, CommandId={CommandId}, IsConfiguration={IsConfiguration}, IsSnapshot={IsSnapshot}, Length={Length}",
            entry.Index,
            entry.Term,
            entry.CommandId,
            entry.IsConfiguration,
            entry.IsSnapshot,
            entry.Length);

        MarkSkippedContext(entry.Context);
        return true;
    }

    private static void MarkSkippedContext(object? context)
    {
        switch (context)
        {
            case CommandApplyContext applyContext:
                applyContext.SetApplied();
                break;

            case KeyValueCommandResult result:
                SetSkipped(result);
                break;

            case KeyValueCommandBatchContext batchContext:
                foreach (var result in batchContext.Results)
                    SetSkipped(result);
                break;

            case IReadOnlyList<KeyValueCommandResult> results:
                foreach (var result in results)
                    SetSkipped(result);
                break;
        }
    }

    private static void SetSkipped(KeyValueCommandResult result)
    {
        if (result.Status == KeyValueCommandStatus.None)
            result.SetError(KeyValueCommandStatus.NotCommitted, SkippedIncompatibleKeyValueMessage);
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var snapshot = _state.Capture();
        Volatile.Write(ref _isSnapshotting, 1);
        _logger?.LogInformation(
            "Persisting SlimData snapshot. KeyValues={KeyValues}, Hashsets={Hashsets}, Queues={Queues}, PayloadBytes={PayloadBytes}, ManagedMemoryBytes={ManagedMemoryBytes}",
            snapshot.KeyValues.Count,
            snapshot.Hashsets.Count,
            snapshot.Queues.Count,
            snapshot.PayloadBytes,
            GC.GetTotalMemory(forceFullCollection: false));

        try
        {
            await SlimDataSnapshotSerializer.WriteAsync(writer, snapshot, token).ConfigureAwait(false);
            Volatile.Write(ref _lastSnapshotSizeBytes, snapshot.PayloadBytes);
        }
        finally
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            Volatile.Write(ref _lastSnapshotDurationMilliseconds, elapsed);
            Volatile.Write(ref _isSnapshotting, 0);
            _logger?.LogInformation(
                "SlimData snapshot persisted. DurationMilliseconds={DurationMilliseconds}, PayloadBytes={PayloadBytes}, ManagedMemoryBytes={ManagedMemoryBytes}",
                elapsed,
                snapshot.PayloadBytes,
                GC.GetTotalMemory(forceFullCollection: false));
        }
    }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        var startedAt = Stopwatch.GetTimestamp();
        Volatile.Write(ref _isRestoring, 1);
        _state.Reset();
        _logger?.LogInformation(
            "Restoring SlimData snapshot. File={SnapshotFile}, FileBytes={FileBytes}, ManagedMemoryBytes={ManagedMemoryBytes}",
            snapshotFile.FullName,
            snapshotFile.Length,
            GC.GetTotalMemory(forceFullCollection: false));

        try
        {
            await using var stream = snapshotFile.Open(new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });

            var reader = IAsyncBinaryReader.Create(stream, new byte[Environment.SystemPageSize]);
            var snapshot = await SlimDataSnapshotSerializer.ReadAsync(reader, token).ConfigureAwait(false);
            _state.Replace(snapshot);
            if (TryGetSnapshotIndex(snapshotFile, out var snapshotIndex))
                Volatile.Write(ref _lastSnapshotRequestIndex, snapshotIndex);
            Volatile.Write(ref _lastSnapshotSizeBytes, snapshotFile.Length);
        }
        finally
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            Volatile.Write(ref _lastSnapshotDurationMilliseconds, elapsed);
            Volatile.Write(ref _isRestoring, 0);
            _logger?.LogInformation(
                "SlimData snapshot restore completed. DurationMilliseconds={DurationMilliseconds}, ManagedMemoryBytes={ManagedMemoryBytes}",
                elapsed,
                GC.GetTotalMemory(forceFullCollection: false));
        }
    }

    private static bool TryGetSnapshotIndex(FileInfo snapshotFile, out long index)
    {
        index = 0L;
        var fileName = snapshotFile.Name.AsSpan();
        var separator = fileName.IndexOf('-');
        return separator > 0 && long.TryParse(fileName[..separator], out index);
    }
}
