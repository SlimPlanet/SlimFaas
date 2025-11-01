using System.Collections.Immutable;
using DotNext;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Commands;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimData.Commands;

namespace SlimData;
/*
#pragma warning disable DOTNEXT001
public sealed class SlimPersistentState : SimpleStateMachine, ISupplier<SlimDataPayload>
{
    public const string LogLocation = "logLocation";

    private readonly SlimDataState _state = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableList<QueueElement>>.Empty
    );

    public CommandInterpreter Interpreter { get; }

    // Chemin vers un sous-dossier "db" (comme dans l'exemple DotNext)
    public SlimPersistentState(string location)
        : this(new DirectoryInfo(Path.GetFullPath(Path.Combine(location ?? string.Empty, "db"))))
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

    public SlimDataState SlimDataState => _state;

    SlimDataPayload ISupplier<SlimDataPayload>.Invoke()
        => new()
        {
            KeyValues = _state.KeyValues,
            Hashsets = _state.Hashsets,
            Queues  = _state.Queues
        };

    // Applique les entrées de log via l'interpréteur existant.
    // On force un snapshot toutes les 10 entrées pour rester simple (modifiable selon ton besoin).
    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.Length == 0L)
            return false;

        await Interpreter.InterpretAsync(entry, token).ConfigureAwait(false);
        return entry.Index % 1000L is 0; // snapshot périodique simple
    }

    // Persiste un snapshot en sérialisant un LogSnapshotCommand (réutilise ta sérialisation existante).
    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        // Construction d’un LogSnapshotCommand à partir de l’état courant
        var keysValues = new Dictionary<string, ReadOnlyMemory<byte>>(_state.KeyValues.ToDictionary(kv => kv.Key, kv => kv.Value));

        var newQueues = new Dictionary<string, List<QueueElement>>();
        foreach (var queue in _state.Queues)
            newQueues[queue.Key] = queue.Value.ToList();

        var newHashsets = new Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>>();
        foreach (var hs in _state.Hashsets)
            newHashsets[hs.Key] = hs.Value.ToDictionary(kv => kv.Key, kv => kv.Value);

        var snapshotCommand = new LogSnapshotCommand(keysValues, newHashsets, newQueues);

        // Écrit le snapshot via ton format binaire existant
        await snapshotCommand.WriteToAsync(writer, token).ConfigureAwait(false);
    }

    // Restaure l’état depuis un fichier de snapshot en relisant le LogSnapshotCommand
    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        await using var stream = snapshotFile.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        // Crée un PipeReader depuis le Stream
        var pipeReader = System.IO.Pipelines.PipeReader.Create(stream);

        // Utilise le PipeReader pour créer l'IAsyncBinaryReader
        var reader = IAsyncBinaryReader.Create(pipeReader);

        // Lecture de ton format binaire existant
        var command = await LogSnapshotCommand.ReadFromAsync(reader, token).ConfigureAwait(false);

        // Applique la commande de snapshot à l’état en mémoire
        // (équivalent à ton DoHandleSnapshotAsync)
        _state.KeyValues = command.keysValues.ToImmutableDictionary();

        var queues = ImmutableDictionary<string, ImmutableList<QueueElement>>.Empty;
        foreach (var q in command.queues)
            queues = queues.SetItem(q.Key, q.Value.ToImmutableList());
        _state.Queues = queues;

        var hashsets = ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty;
        foreach (var hs in command.hashsets)
            hashsets = hashsets.SetItem(hs.Key, hs.Value.ToImmutableDictionary());
        _state.Hashsets = hashsets;
    }
}
#pragma warning restore DOTNEXT001*/


public sealed class SlimPersistentState : MemoryBasedStateMachine, ISupplier<SlimDataPayload>
{
    public const string LogLocation = "logLocation";

    private readonly SlimDataState _state = new(
        ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
        ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
        ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
        );
    public CommandInterpreter Interpreter { get; }

    public  SlimPersistentState(string path)
        : base(path, 50, new Options { InitialPartitionSize = 50 * 80, UseCaching = true, UseLegacyBinaryFormat = false })
    {
        Interpreter = SlimDataInterpreter.InitInterpreter(_state);
    }

    public SlimPersistentState(IConfiguration configuration)
        : this(configuration[LogLocation])
    {
    }

    public SlimDataState SlimDataState
    {
        get
        {
            return _state;
        }
    }
    SlimDataPayload ISupplier<SlimDataPayload>.Invoke()
    {
        return new SlimDataPayload()
        {
            KeyValues = _state.KeyValues,
            Hashsets = _state.Hashsets,
            Queues = _state.Queues
        };
    }

    private async ValueTask UpdateValue(LogEntry entry)
    {
        await Interpreter.InterpretAsync(entry);
    }

    protected override ValueTask ApplyAsync(LogEntry entry)
    {
        return entry.Length == 0L ? new ValueTask() : UpdateValue(entry);
    }

    protected override SnapshotBuilder CreateSnapshotBuilder(in SnapshotBuilderContext context)
    {
        return new SimpleSnapshotBuilder(context);
    }

    private sealed class SimpleSnapshotBuilder : IncrementalSnapshotBuilder
    {
        private readonly SlimDataState _state =  new(
            ImmutableDictionary<string, ImmutableDictionary<string, ReadOnlyMemory<byte>>>.Empty,
            ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty,
            ImmutableDictionary<string, ImmutableArray<QueueElement>>.Empty
            );
        private readonly CommandInterpreter _interpreter;

        public SimpleSnapshotBuilder(in SnapshotBuilderContext context)
            : base(context)
        {
            _interpreter = SlimDataInterpreter.InitInterpreter(_state);
        }

        protected override async ValueTask ApplyAsync(LogEntry entry)
        {
            await _interpreter.InterpretAsync(entry);
        }

        public override async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        {
            var keysValues = new Dictionary<string, ReadOnlyMemory<byte>>(_state.KeyValues.ToDictionary(kv => kv.Key, kv => kv.Value));
            var queues =  _state.Queues;
            var newQueues = new Dictionary<string, List<QueueElement>>();
            var hashsets = _state.Hashsets;
            var newHashsets = new Dictionary<string, Dictionary<string, ReadOnlyMemory<byte>>>();
            
            foreach (var hashset in hashsets)
            {
                newHashsets[hashset.Key] = hashset.Value.ToDictionary(kv => kv.Key, kv => kv.Value);
            }
            
            foreach (var queue in queues)
            {
                newQueues[queue.Key] = queue.Value.ToList();
            }
            
            LogSnapshotCommand command = new(keysValues, newHashsets, newQueues);
            await command.WriteToAsync(writer, token).ConfigureAwait(false);
        }
    }
}