using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace SlimData;

#pragma warning disable CA2252 // Implement the preview WAL interfaces already used by the Raft runtime.
internal sealed class DurableWriteAheadLog : WriteAheadLog, IPersistentState
{
    private readonly Func<CancellationToken, Task> _flush;

    internal DurableWriteAheadLog(Options options, IStateMachine stateMachine)
        : this(options, stateMachine, null)
    {
    }

    internal DurableWriteAheadLog(Options options, IStateMachine stateMachine,
        Func<CancellationToken, Task>? flush) : base(options, stateMachine)
    {
        _flush = flush ?? FlushAsync;
    }

    // DotNext's buffered Append completes before its background flusher writes
    // the recovery checkpoint. Raft must not count that volatile copy toward a
    // quorum: ENOSPC followed by a crash can otherwise forget an acknowledged
    // entry, allowing a later leader to overwrite it.
    public new async ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
        where TEntry : IRaftLogEntry
    {
        long index = await base.AppendAsync(entry, token).ConfigureAwait(false);
        await _flush(token).ConfigureAwait(false);
        return index;
    }

    public new async ValueTask AppendAsync<TEntry>(TEntry entry, long startIndex,
        CancellationToken token = default) where TEntry : IRaftLogEntry
    {
        await base.AppendAsync(entry, startIndex, token).ConfigureAwait(false);
        await _flush(token).ConfigureAwait(false);
    }

    public new async ValueTask AppendAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex,
        bool skipCommitted = false, CancellationToken token = default) where TEntry : IRaftLogEntry
    {
        await base.AppendAsync(entries, startIndex, skipCommitted, token).ConfigureAwait(false);
        await _flush(token).ConfigureAwait(false);
    }
}
#pragma warning restore CA2252
