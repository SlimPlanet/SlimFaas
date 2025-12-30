using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal sealed class KeyedAsyncLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _locks = new(StringComparer.Ordinal);

    // Global in-flight bytes limiter
    private readonly long _maxInFlightBytes;
    private long _inFlightBytes;
    private readonly LinkedList<BytesWaiter> _byteWaiters = new();

    internal sealed class Entry
    {
        public int RefCount;
        public readonly SemaphoreSlim Semaphore = new(1, 1);
    }

    private sealed class BytesWaiter
    {
        public required long Bytes;
        public required TaskCompletionSource<bool> Tcs;
        public LinkedListNode<BytesWaiter>? Node;
        public CancellationTokenRegistration Ctr;
    }

    public KeyedAsyncLock(long maxInFlightBytes)
    {
        if (maxInFlightBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxInFlightBytes));
        _maxInFlightBytes = maxInFlightBytes;
    }

    // Compat : si tu ne veux pas compter des bytes (ou inconnu) -> bytes=0
    public ValueTask<Releaser> AcquireAsync(string key, CancellationToken ct) =>
        AcquireAsync(key, bytesToReserve: 0, ct);

    /// <summary>
    /// Acquire le lock par clé + réserve un budget global (bytesToReserve) tant que l’upload est en cours.
    /// Règle spéciale: si bytesToReserve > maxInFlightBytes, on l’autorise uniquement si aucun autre upload n’est en cours.
    /// </summary>
    public async ValueTask<Releaser> AcquireAsync(string key, long bytesToReserve, CancellationToken ct)
    {
        if (key is null) throw new ArgumentNullException(nameof(key));
        if (bytesToReserve < 0) throw new ArgumentOutOfRangeException(nameof(bytesToReserve));

        Entry entry;
        lock (_gate)
        {
            if (!_locks.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _locks.Add(key, entry);
            }
            entry.RefCount++;
        }

        try
        {
            // 1) lock par clé
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);

            // 2) budget global (bytes "in flight")
            if (bytesToReserve > 0)
                await ReserveBytesAsync(bytesToReserve, ct).ConfigureAwait(false);

            return new Releaser(this, key, entry, bytesToReserve);
        }
        catch
        {
            // Si on a pris le sémaphore, on le relâche.
            // Note: ReserveBytesAsync ne "réserve" qu'au moment où ça passe.
            TryReleaseKeySemaphore(entry);
            ReleaseRef(key, entry);
            throw;
        }
    }

    private static void TryReleaseKeySemaphore(Entry entry)
    {
        // Si WaitAsync a été acquis, CurrentCount == 0.
        // Si on n'a pas acquis, Release() lèverait.
        // On évite l’exception en checkant.
        if (entry.Semaphore.CurrentCount == 0)
        {
            try { entry.Semaphore.Release(); }
            catch { /* ignore: ultra rare race, on ne veut pas masquer l’exception d’origine */ }
        }
    }

    private async ValueTask ReserveBytesAsync(long bytes, CancellationToken ct)
    {
        // Fast-path
        BytesWaiter? waiterToAwait = null;

        lock (_gate)
        {
            if (CanStartNow_NoLock(bytes))
            {
                _inFlightBytes += bytes;
                return;
            }

            var waiter = new BytesWaiter
            {
                Bytes = bytes,
                Tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            };

            waiter.Node = _byteWaiters.AddLast(waiter);

            if (ct.CanBeCanceled)
            {
                waiter.Ctr = ct.Register(static state =>
                {
                    var tuple = (Tuple<KeyedAsyncLock, BytesWaiter, CancellationToken>)state!;
                    tuple.Item1.CancelWaiter(tuple.Item2, tuple.Item3);
                }, Tuple.Create(this, waiter, ct));
            }

            waiterToAwait = waiter;
        }

        try
        {
            await waiterToAwait!.Tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            // Si ça a été réveillé normalement, Ctr est déjà dispo dans DrainWaiters.
            // Si ça a été annulé, on dispose ici par sécurité.
            waiterToAwait!.Ctr.Dispose();
        }
    }

    private void CancelWaiter(BytesWaiter waiter, CancellationToken ct)
    {
        TaskCompletionSource<bool>? toCancel = null;

        lock (_gate)
        {
            if (waiter.Node is not null)
            {
                _byteWaiters.Remove(waiter.Node);
                waiter.Node = null;
                toCancel = waiter.Tcs;
            }
        }

        toCancel?.TrySetCanceled(ct);
    }

    private bool CanStartNow_NoLock(long bytes)
    {
        // Règle spéciale: un fichier plus gros que le max peut passer uniquement si on est seul.
        if (bytes > _maxInFlightBytes)
            return _inFlightBytes == 0;

        // Règle normale: on ne dépasse pas le budget.
        return _inFlightBytes + bytes <= _maxInFlightBytes;
    }

    private void Release(string key, Entry entry, long bytesReserved)
    {
        // 1) libérer le budget global en premier (ça réveille potentiellement des uploads)
        if (bytesReserved > 0)
            ReleaseBytes(bytesReserved);

        // 2) libérer le lock par clé
        entry.Semaphore.Release();

        // 3) refcount/déalloc du sémaphore
        ReleaseRef(key, entry);
    }

    private void ReleaseBytes(long bytes)
    {
        List<BytesWaiter>? toWake = null;

        lock (_gate)
        {
            _inFlightBytes -= bytes;
            if (_inFlightBytes < 0) _inFlightBytes = 0;

            // Réveille autant de waiters que possible.
            // Stratégie "work-conserving": on cherche le 1er qui fit, pas forcément FIFO strict.
            while (_byteWaiters.Count > 0)
            {
                LinkedListNode<BytesWaiter>? chosenNode = null;

                for (var node = _byteWaiters.First; node is not null; node = node.Next)
                {
                    if (CanStartNow_NoLock(node.Value.Bytes))
                    {
                        chosenNode = node;
                        break;
                    }
                }

                if (chosenNode is null)
                    break;

                var chosen = chosenNode.Value;
                _byteWaiters.Remove(chosenNode);
                chosen.Node = null;

                _inFlightBytes += chosen.Bytes;

                toWake ??= new List<BytesWaiter>(4);
                toWake.Add(chosen);
            }
        }

        if (toWake is not null)
        {
            foreach (var w in toWake)
            {
                w.Ctr.Dispose();
                w.Tcs.TrySetResult(true);
            }
        }
    }

    private void ReleaseRef(string key, Entry entry)
    {
        bool dispose = false;

        lock (_gate)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                _locks.Remove(key);
                dispose = true;
            }
        }

        if (dispose)
            entry.Semaphore.Dispose();
    }

    public struct Releaser : IAsyncDisposable, IDisposable
    {
        private KeyedAsyncLock? _owner;
        private string? _key;
        private Entry? _entry;
        private long _bytesReserved;

        internal Releaser(KeyedAsyncLock owner, string key, Entry entry, long bytesReserved)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
            _bytesReserved = bytesReserved;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;

            var key = _key!;
            var entry = _entry!;
            var bytes = Interlocked.Exchange(ref _bytesReserved, 0);

            _key = null;
            _entry = null;

            owner.Release(key, entry, bytes);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    // Helper pratique
    public static long MegaBytes(long mb) => checked(mb * 1024L * 1024L);
}
