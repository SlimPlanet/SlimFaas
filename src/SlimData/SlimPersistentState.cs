using System.Collections.Immutable;
using DotNext;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData;

public sealed class SlimPersistentState : SimpleStateMachine, ISupplier<SlimDataPayload>
{
    public const string LogLocation = "SlimData:LogLocation";
    public const string UsePersistentConfigurationStorage = "SlimData:UsePersistentConfigurationStorage";

    private readonly SlimDataState _state = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty);

    public SlimPersistentState(string location)
        : this(new DirectoryInfo(Path.GetFullPath(Path.Combine(NormalizeLocation(location), "db"))))
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

    private static string NormalizeLocation(string? location)
        => string.IsNullOrWhiteSpace(location)
            ? Path.Combine(Path.GetTempPath(), "SlimData", Guid.NewGuid().ToString("N"))
            : location;

    public CommandInterpreter Interpreter { get; }

    public SlimDataState SlimDataState => _state;

    SlimDataPayload ISupplier<SlimDataPayload>.Invoke()
        => new()
        {
            KeyValues = _state.KeyValues,
            Hashsets = _state.Hashsets,
            Queues = _state.Queues
        };

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
            throw new InvalidDataException(
                $"Failed to interpret SlimData Raft log entry. Index={entry.Index}, Term={entry.Term}, CommandId={entry.CommandId?.ToString() ?? "null"}, IsConfiguration={entry.IsConfiguration}, IsSnapshot={entry.IsSnapshot}, Length={entry.Length?.ToString() ?? "null"}.",
                ex);
        }

        if (entry.Context is CommandApplyContext applyContext)
            applyContext.SetApplied();

        return entry.Index % 1000L is 0;
    }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        var keyValues = new Dictionary<string, ReadOnlyMemory<byte>>(
            _state.KeyValues.ToDictionary(kv => kv.Key, kv => kv.Value));

        var queues = new Dictionary<string, List<QueueElement>>(_state.Queues.Count);
        foreach (var queue in _state.Queues)
            queues[queue.Key] = queue.Value.ToList();

        var hashsets = new Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>>(_state.Hashsets.Count);
        foreach (var hashset in _state.Hashsets)
            hashsets[hashset.Key] = hashset.Value.ToDictionary(kv => kv.Key, kv => kv.Value);

        var snapshotCommand = new LogSnapshotCommand(keyValues, hashsets, queues);
        await snapshotCommand.WriteToAsync(writer, token).ConfigureAwait(false);
    }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        await using var stream = snapshotFile.Open(new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        var reader = IAsyncBinaryReader.Create(stream, new byte[Environment.SystemPageSize]);
        var snapshotCommand = await LogSnapshotCommand.ReadFromAsync(reader, token).ConfigureAwait(false);
        await SlimDataInterpreter.DoHandleSnapshotAsync(snapshotCommand, _state).ConfigureAwait(false);
    }
}
