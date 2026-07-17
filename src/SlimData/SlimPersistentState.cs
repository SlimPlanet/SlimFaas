using System.Buffers;
using System.Collections.Concurrent;
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

public readonly record struct SlimDataSkippedCommandMetric(int CommandId, long Count);

public readonly record struct SlimDataRaftSafetyMetrics(
    long SizeViolations,
    long FormatViolations,
    long LastSkippedIndex,
    long LastSkippedTerm,
    int LastSkippedCommandId,
    long LastSkippedLength);

public enum SlimDataSnapshotTrigger
{
    None,
    Entries,
    Bytes,
    Incompatible
}

public sealed class SlimPersistentState : SimpleStateMachine, ISupplier<SlimDataPayload>
{
    public const string LogLocation = "SlimData:LogLocation";
    public const string UsePersistentConfigurationStorage = "SlimData:UsePersistentConfigurationStorage";
    public const string SnapshotIntervalEntries = "SlimData:SnapshotIntervalEntries";
    public const string SnapshotIntervalBytes = "SlimData:SnapshotIntervalBytes";
    public const int DefaultSnapshotIntervalEntries = 5000;
    public const long DefaultSnapshotIntervalBytes = 64L * 1024L * 1024L;
    private const string SkippedIncompatibleCommandMessage =
        "Skipped incompatible SlimData Raft log entry.";

    private readonly SlimDataState _state = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);
    private readonly ILogger<SlimPersistentState>? _logger;
    private readonly int _snapshotIntervalEntries;
    private readonly long _snapshotIntervalBytes;
    private readonly ConcurrentDictionary<int, long> _skippedCommands = new();
    private long _walEntriesSinceSnapshot;
    private long _walBytesSinceSnapshot;
    private int _lastSnapshotTrigger;
    private int _incompatibleCompactionRequested;
    private int _isRestoring;
    private int _isSnapshotting;
    private long _lastSnapshotDurationMilliseconds;
    private long _lastSnapshotSizeBytes;
    private long _sizeViolations;
    private long _formatViolations;
    private long _lastSkippedIndex = -1L;
    private long _lastSkippedTerm = -1L;
    private int _lastSkippedCommandId = -1;
    private long _lastSkippedLength = -1L;

    public SlimPersistentState(string location)
        : this(location, DefaultSnapshotIntervalEntries, DefaultSnapshotIntervalBytes, null)
    {
    }

    public SlimPersistentState(string location, ILogger<SlimPersistentState>? logger)
        : this(location, DefaultSnapshotIntervalEntries, DefaultSnapshotIntervalBytes, logger)
    {
    }

    internal SlimPersistentState(string location, int snapshotIntervalEntries, ILogger<SlimPersistentState>? logger = null)
        : this(location, snapshotIntervalEntries, DefaultSnapshotIntervalBytes, logger)
    {
    }

    internal SlimPersistentState(
        string location,
        int snapshotIntervalEntries,
        long snapshotIntervalBytes,
        ILogger<SlimPersistentState>? logger = null)
        : this(
            new DirectoryInfo(Path.GetFullPath(Path.Combine(NormalizeLocation(location), "db"))),
            snapshotIntervalEntries,
            snapshotIntervalBytes,
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
            configuration.GetValue(SnapshotIntervalBytes, DefaultSnapshotIntervalBytes),
            logger)
    {
    }

    private SlimPersistentState(
        DirectoryInfo location,
        int snapshotIntervalEntries,
        long snapshotIntervalBytes,
        ILogger<SlimPersistentState>? logger)
        : base(location)
    {
        _snapshotIntervalEntries = snapshotIntervalEntries > 0
            ? snapshotIntervalEntries
            : throw new ArgumentOutOfRangeException(
                nameof(snapshotIntervalEntries),
                snapshotIntervalEntries,
                "The snapshot entry interval must be strictly positive.");
        _snapshotIntervalBytes = snapshotIntervalBytes > 0L
            ? snapshotIntervalBytes
            : throw new ArgumentOutOfRangeException(
                nameof(snapshotIntervalBytes),
                snapshotIntervalBytes,
                "The snapshot byte interval must be strictly positive.");
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

    public long WalBytesSinceSnapshot => Volatile.Read(ref _walBytesSinceSnapshot);

    public SlimDataSnapshotTrigger LastSnapshotTrigger
        => (SlimDataSnapshotTrigger)Volatile.Read(ref _lastSnapshotTrigger);

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

    public IReadOnlyList<SlimDataSkippedCommandMetric> GetSkippedCommandMetrics()
        => _skippedCommands
            .OrderBy(static item => item.Key)
            .Select(static item => new SlimDataSkippedCommandMetric(item.Key, item.Value))
            .ToArray();

    public SlimDataRaftSafetyMetrics GetRaftSafetyMetrics()
        => new(
            Volatile.Read(ref _sizeViolations),
            Volatile.Read(ref _formatViolations),
            Volatile.Read(ref _lastSkippedIndex),
            Volatile.Read(ref _lastSkippedTerm),
            Volatile.Read(ref _lastSkippedCommandId),
            Volatile.Read(ref _lastSkippedLength));

    SlimDataPayload ISupplier<SlimDataPayload>.Invoke()
        => _state.CapturePayload();

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.IsConfiguration || entry.IsSnapshot)
            return false;

        if (entry.Length == 0L && entry.CommandId is null)
            return false;

        if (!SlimDataCommandCodec.IsSupportedCommandId(entry.CommandId))
        {
            return SkipIncompatibleEntry(
                entry,
                SlimDataCommandViolation.UnknownCommand,
                "Unknown SlimData Raft command ID.");
        }

        if (entry.Length is null)
        {
            return SkipIncompatibleEntry(
                entry,
                SlimDataCommandViolation.InvalidLength,
                "SlimData Raft command does not declare its serialized length.");
        }

        if (entry.Length is > SlimDataCommandCodec.MaxCommandBytes +
            SlimDataCommandCodec.MaxRecoverableWalPaddingBytes)
        {
            return SkipIncompatibleEntry(
                entry,
                SlimDataCommandViolation.TooLarge,
                "SlimData Raft command exceeds the maximum allowed size.");
        }

        if (entry.Length is < 0L || entry.Length < SlimDataCommandCodec.HeaderLength)
        {
            return SkipIncompatibleEntry(
                entry,
                entry.Length < 0L
                    ? SlimDataCommandViolation.InvalidLength
                    : SlimDataCommandViolation.Truncated,
                entry.Length < 0L
                    ? "SlimData Raft command has a negative serialized length."
                    : $"SlimData Raft command is shorter than its {SlimDataCommandCodec.HeaderLength}-byte envelope.");
        }

        IRaftLogEntry applicationEntry = entry;
        var recovered = TryRecoverZeroPrefixedCurrentEntry(
            entry,
            out var recoveredEntry,
            out var discardedPrefixBytes);
        if (recovered)
        {
            applicationEntry = recoveredEntry;
            _logger?.LogDebug(
                "Recovered zero-prefixed SlimData Raft log entry. Index={Index}, Term={Term}, CommandId={CommandId}, OriginalLength={OriginalLength}, DiscardedPrefixBytes={DiscardedPrefixBytes}",
                entry.Index,
                entry.Term,
                entry.CommandId,
                entry.Length,
                discardedPrefixBytes);
        }
        else if (entry.Length is > SlimDataCommandCodec.MaxCommandBytes)
        {
            return SkipIncompatibleEntry(
                entry,
                SlimDataCommandViolation.TooLarge,
                "SlimData Raft command exceeds the maximum allowed size.");
        }

        try
        {
            await Interpreter.InterpretAsync(applicationEntry, entry.Context, token).ConfigureAwait(false);
        }
        catch (SlimDataCommandFormatException ex)
        {
            return SkipIncompatibleEntry(entry, ex.Violation, ex.Message, ex);
        }
        catch (Exception ex) when (ex is InvalidDataException || SlimDataCommandCodec.IsStructuralException(ex))
        {
            return SkipIncompatibleEntry(
                entry,
                ex is EndOfStreamException
                    ? SlimDataCommandViolation.Truncated
                    : SlimDataCommandViolation.InvalidFormat,
                "SlimData Raft command has an invalid binary format.",
                ex);
        }

        if (entry.Context is CommandApplyContext applyContext)
            applyContext.SetApplied();

        return ShouldCreateSnapshot(entry.Length.GetValueOrDefault());
    }

    private bool ShouldCreateSnapshot(long entryLength)
    {
        var entries = Interlocked.Increment(ref _walEntriesSinceSnapshot);
        var bytes = Interlocked.Add(ref _walBytesSinceSnapshot, entryLength);
        var trigger = bytes >= _snapshotIntervalBytes
            ? SlimDataSnapshotTrigger.Bytes
            : entries >= _snapshotIntervalEntries
                ? SlimDataSnapshotTrigger.Entries
                : SlimDataSnapshotTrigger.None;

        if (trigger is SlimDataSnapshotTrigger.None)
            return false;

        ResetSnapshotWindow(trigger);
        return true;
    }

    private bool ShouldCompactIncompatibleEntry()
    {
        if (Interlocked.CompareExchange(ref _incompatibleCompactionRequested, 1, 0) is not 0)
            return false;

        ResetSnapshotWindow(SlimDataSnapshotTrigger.Incompatible);
        return true;
    }

    private void ResetSnapshotWindow(SlimDataSnapshotTrigger trigger)
    {
        Interlocked.Exchange(ref _walEntriesSinceSnapshot, 0L);
        Interlocked.Exchange(ref _walBytesSinceSnapshot, 0L);
        Volatile.Write(ref _lastSnapshotTrigger, (int)trigger);
    }

    private bool SkipIncompatibleEntry(
        LogEntry entry,
        SlimDataCommandViolation violation,
        string reason,
        Exception? exception = null)
    {
        var payloadDiagnostics = GetPayloadDiagnostics(entry);
        _logger?.LogWarning(
            exception,
            "Skipping incompatible SlimData Raft log entry. Index={Index}, Term={Term}, CommandId={CommandId}, Length={Length}, Violation={Violation}, LeadingZeroBytes={LeadingZeroBytes}, CurrentEnvelopeOffset={CurrentEnvelopeOffset}, Reason={Reason}",
            entry.Index,
            entry.Term,
            entry.CommandId,
            entry.Length,
            violation,
            payloadDiagnostics.LeadingZeroBytes,
            payloadDiagnostics.CurrentEnvelopeOffset,
            reason);

        RecordSkippedEntry(entry, violation);
        MarkSkippedContext(entry.Context);
        return ShouldCompactIncompatibleEntry();
    }

    private static bool TryRecoverZeroPrefixedCurrentEntry(
        LogEntry entry,
        out BinaryLogEntry recoveredEntry,
        out int discardedPrefixBytes)
    {
        recoveredEntry = default;
        discardedPrefixBytes = 0;
        if (!entry.TryGetPayload(out var payload) || payload.IsEmpty)
            return false;

        var leadingZeroBytes = CountLeadingZeroBytes(payload);
        if (leadingZeroBytes <= 0L ||
            leadingZeroBytes > SlimDataCommandCodec.MaxRecoverableWalPaddingBytes ||
            payload.Length - leadingZeroBytes < SlimDataCommandCodec.HeaderLength)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[SlimDataCommandCodec.HeaderLength];
        payload.Slice(leadingZeroBytes, SlimDataCommandCodec.HeaderLength).CopyTo(header);
        if (!SlimDataCommandCodec.HasCurrentEnvelope(header))
            return false;

        var recoveredPayload = payload.Slice(leadingZeroBytes).ToArray();
        SlimDataCommandCodec.ValidateCommandLength(
            recoveredPayload.Length,
            $"SlimData command {entry.CommandId}");
        SlimDataCommandCodec.ValidateCurrentEnvelope(
            recoveredPayload,
            $"SlimData command {entry.CommandId}");
        recoveredEntry = new BinaryLogEntry
        {
            CommandId = entry.CommandId,
            Term = entry.Term,
            Content = recoveredPayload,
            Context = entry.Context
        };
        discardedPrefixBytes = checked((int)leadingZeroBytes);
        return true;
    }

    private static PayloadDiagnostics GetPayloadDiagnostics(LogEntry entry)
    {
        if (!entry.TryGetPayload(out var payload) || payload.IsEmpty)
            return new(0L, -1);

        var leadingZeroBytes = CountLeadingZeroBytes(payload);
        var currentEnvelopeOffset = -1;
        if (leadingZeroBytes <= int.MaxValue &&
            payload.Length - leadingZeroBytes >= SlimDataCommandCodec.HeaderLength)
        {
            Span<byte> header = stackalloc byte[SlimDataCommandCodec.HeaderLength];
            payload.Slice(leadingZeroBytes, SlimDataCommandCodec.HeaderLength).CopyTo(header);
            if (SlimDataCommandCodec.HasCurrentEnvelope(header))
                currentEnvelopeOffset = checked((int)leadingZeroBytes);
        }

        return new(leadingZeroBytes, currentEnvelopeOffset);
    }

    private static long CountLeadingZeroBytes(ReadOnlySequence<byte> payload)
    {
        long leadingZeroBytes = 0L;
        foreach (var segment in payload)
        {
            foreach (var value in segment.Span)
            {
                if (value != 0)
                    return leadingZeroBytes;
                leadingZeroBytes++;
            }
        }

        return leadingZeroBytes;
    }

    private readonly record struct PayloadDiagnostics(long LeadingZeroBytes, int CurrentEnvelopeOffset);

    private void RecordSkippedEntry(LogEntry entry, SlimDataCommandViolation violation)
    {
        var commandId = entry.CommandId ?? -1;
        _skippedCommands.AddOrUpdate(commandId, 1L, static (_, count) => count + 1L);

        if (violation is SlimDataCommandViolation.TooLarge)
            Interlocked.Increment(ref _sizeViolations);
        else
            Interlocked.Increment(ref _formatViolations);

        Volatile.Write(ref _lastSkippedIndex, entry.Index);
        Volatile.Write(ref _lastSkippedTerm, entry.Term);
        Volatile.Write(ref _lastSkippedCommandId, commandId);
        Volatile.Write(ref _lastSkippedLength, entry.Length ?? -1L);
    }

    private static void MarkSkippedContext(object? context)
    {
        switch (context)
        {
            case CommandApplyContext applyContext:
                applyContext.SetSkipped(SkippedIncompatibleCommandMessage);
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
            result.SetError(KeyValueCommandStatus.NotCommitted, SkippedIncompatibleCommandMessage);
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
        ResetSnapshotWindow(SlimDataSnapshotTrigger.None);
        Volatile.Write(ref _incompatibleCompactionRequested, 0);
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

}
