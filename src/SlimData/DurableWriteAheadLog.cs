using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace SlimData;

#pragma warning disable CA2252 // Implement the preview WAL interfaces already used by the Raft runtime.
internal sealed class DurableWriteAheadLog : WriteAheadLog, IPersistentState
{
    private readonly Func<CancellationToken, Task> _flush;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    internal DurableWriteAheadLog(Options options, IStateMachine stateMachine)
        : this(options, stateMachine, null)
    {
    }

    internal DurableWriteAheadLog(Options options, IStateMachine stateMachine,
        Func<CancellationToken, Task>? flush) : base(options, stateMachine)
    {
        _flush = flush ?? base.FlushAsync;
    }

    // DotNext's buffered Append completes before its background flusher writes
    // the recovery checkpoint. Raft must not count that volatile copy toward a
    // quorum: ENOSPC followed by a crash can otherwise forget an acknowledged
    // entry, allowing a later leader to overwrite it.
    public new async ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
        where TEntry : IRaftLogEntry
    {
        long index = await base.AppendAsync(entry, token).ConfigureAwait(false);
        await FlushAppendedAsync(token).ConfigureAwait(false);
        return index;
    }

    public new async ValueTask AppendAsync<TEntry>(TEntry entry, long startIndex,
        CancellationToken token = default) where TEntry : IRaftLogEntry
    {
        await base.AppendAsync(entry, startIndex, token).ConfigureAwait(false);
        await FlushAppendedAsync(token).ConfigureAwait(false);
    }

    public new async ValueTask AppendAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex,
        bool skipCommitted = false, CancellationToken token = default) where TEntry : IRaftLogEntry
    {
        await base.AppendAsync(entries, startIndex, skipCommitted, token).ConfigureAwait(false);
        await FlushAppendedAsync(token).ConfigureAwait(false);
    }

    public new async ValueTask<long> CommitAsync(long endIndex, CancellationToken token = default)
    {
        // Base CommitAsync can wait for an unflushed tail too. It must share
        // the append observer's gate, including calls through IAuditTrail.
        await _flushGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await base.CommitAsync(endIndex, token).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public new Task FlushAsync(CancellationToken token = default)
        => FlushAppendedAsync(token).AsTask();

    private async ValueTask FlushAppendedAsync(CancellationToken token)
    {
        // The underlying flush-completion ValueTask source supports one waiter.
        // Keep appends concurrent, but serialize append/commit/flush observers;
        // the flusher can still persist their entries together.
        await _flushGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await _flush(token).ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Release();
        }
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        try
        {
            await base.DisposeAsyncCore().ConfigureAwait(false);
        }
        finally
        {
            _flushGate.Dispose();
        }
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
                _flushGate.Dispose();
        }
    }
}
#pragma warning restore CA2252
