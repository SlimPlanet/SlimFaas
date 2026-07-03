using System.Buffers;
using System.Collections.Immutable;
using DotNext;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData;

#pragma warning disable DOTNEXT001
public sealed class SlimPersistentState : SimpleStateMachine, ISupplier<SlimDataPayload>
{
    public const string LogLocation = "SlimData:LogLocation";
    public const string UsePersistentConfigurationStorage = "SlimData:UsePersistentConfigurationStorage";

    // Snapshot cadence: request a snapshot every N applied entries.
    // Uses a running counter rather than entry.Index modulo, because Index is monotonically
    // increasing and modulo-based cadence would misbehave after log truncation.
    private const long SnapshotEvery = 1000L;

    internal const string SnapshotSubDirectory = "db";

    private readonly SlimDataState _state = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

    private long _entriesSinceSnapshot;

    public CommandInterpreter Interpreter { get; }

    public SlimDataState SlimDataState => _state;

    public SlimPersistentState(string location)
        : this(new DirectoryInfo(Path.GetFullPath(Path.Combine(location ?? string.Empty, SnapshotSubDirectory))))
    {
    }

    public SlimPersistentState(IConfiguration configuration)
        : this(configuration[LogLocation] ?? string.Empty)
    {
    }

    private SlimPersistentState(DirectoryInfo location)
        : base(location)
    {
        Interpreter = SlimDataInterpreter.InitInterpreter(_state);
    }

    SlimDataPayload ISupplier<SlimDataPayload>.Invoke()
        => new()
        {
            KeyValues = _state.KeyValues,
            Hashsets = _state.Hashsets,
            Queues = _state.Queues
        };

    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.Length == 0L)
            return false;

        await Interpreter.InterpretAsync(entry, token).ConfigureAwait(false);

        if (++_entriesSinceSnapshot < SnapshotEvery)
            return false;

        _entriesSinceSnapshot = 0;
        return true;
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        var keysValues = _state.KeyValues.ToDictionary(kv => kv.Key, kv => kv.Value);

        var hashsets = new Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>>(_state.Hashsets.Count);
        foreach (var hs in _state.Hashsets)
            hashsets[hs.Key] = hs.Value.ToDictionary(kv => kv.Key, kv => kv.Value);

        var queues = new Dictionary<string, List<QueueElement>>(_state.Queues.Count);
        foreach (var q in _state.Queues)
            queues[q.Key] = q.Value.ToList();

        var snapshot = new LogSnapshotCommand(keysValues, hashsets, queues);
        await snapshot.WriteToAsync(writer, token).ConfigureAwait(false);
    }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        await using var stream = new FileStream(
            snapshotFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var reader = IAsyncBinaryReader.Create(stream, buffer);
            var command = await LogSnapshotCommand.ReadFromAsync(reader, token).ConfigureAwait(false);

            ApplySnapshot(command);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _entriesSinceSnapshot = 0;
    }

    private void ApplySnapshot(LogSnapshotCommand command)
    {

        _state.KeyValues = command.keysValues.ToImmutableDictionary();

        var queues = ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty;
        foreach (var q in command.queues)
            queues = queues.SetItem(q.Key, q.Value.ToImmutableArray());
        _state.Queues = queues;

        var hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty;
        foreach (var hs in command.hashsets)
            hashsets = hashsets.SetItem(hs.Key, hs.Value.ToImmutableDictionary());
        _state.Hashsets = hashsets;
    }
}
#pragma warning restore DOTNEXT001
