namespace SlimData.ClusterFiles;

internal sealed class KeyedAsyncLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _locks = new(StringComparer.Ordinal);
    private readonly long _maxInFlightBytes;
    private readonly int _maxPendingTransfers;
    private readonly TimeSpan _queueWaitTimeout;
    private readonly LinkedList<BytesWaiter> _byteWaiters = new();

    private long _inFlightBytes;
    private int _pendingTransfers;

    internal sealed class Entry
    {
        public int RefCount;
        public readonly SemaphoreSlim Semaphore = new(1, 1);
    }

    private sealed class BytesWaiter
    {
        public required long Bytes;
        public required TaskCompletionSource<bool> Completion;
        public LinkedListNode<BytesWaiter>? Node;
        public CancellationTokenRegistration CancellationRegistration;
    }

    public KeyedAsyncLock(
        long maxInFlightBytes,
        int maxPendingTransfers = 128,
        TimeSpan? queueWaitTimeout = null)
    {
        if (maxInFlightBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxInFlightBytes));
        if (maxPendingTransfers <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxPendingTransfers));

        var timeout = queueWaitTimeout ?? TimeSpan.FromSeconds(30);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(queueWaitTimeout));

        _maxInFlightBytes = maxInFlightBytes;
        _maxPendingTransfers = maxPendingTransfers;
        _queueWaitTimeout = timeout;
    }

    public ValueTask<Releaser> AcquireAsync(string key, CancellationToken ct) =>
        AcquireAsync(key, bytesToReserve: 0, ct);

    public async ValueTask<Releaser> AcquireAsync(string key, long bytesToReserve, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (bytesToReserve < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesToReserve));

        Entry entry;
        lock (_gate)
        {
            if (_pendingTransfers >= _maxPendingTransfers)
            {
                throw new FileTransferCapacityExceededException(
                    $"The file transfer queue is full ({_maxPendingTransfers} pending transfers).");
            }

            _pendingTransfers++;
            if (!_locks.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _locks.Add(key, entry);
            }

            entry.RefCount++;
        }

        var pendingReleased = false;
        var keyAcquired = false;
        var bytesReserved = false;
        using var timeoutSource = new CancellationTokenSource(_queueWaitTimeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutSource.Token);

        try
        {
            await entry.Semaphore.WaitAsync(linkedSource.Token).ConfigureAwait(false);
            keyAcquired = true;

            if (bytesToReserve > 0)
            {
                await ReserveBytesAsync(bytesToReserve, linkedSource.Token).ConfigureAwait(false);
                bytesReserved = true;
            }

            ReleasePendingTransfer();
            pendingReleased = true;
            return new Releaser(this, key, entry, bytesToReserve);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            if (bytesReserved)
                ReleaseBytes(bytesToReserve);
            if (keyAcquired)
                entry.Semaphore.Release();
            ReleaseRef(key, entry);

            throw new FileTransferCapacityExceededException(
                $"Timed out after {_queueWaitTimeout.TotalSeconds:0.###} seconds waiting for file transfer capacity.");
        }
        catch
        {
            if (bytesReserved)
                ReleaseBytes(bytesToReserve);
            if (keyAcquired)
                entry.Semaphore.Release();
            ReleaseRef(key, entry);
            throw;
        }
        finally
        {
            if (!pendingReleased)
                ReleasePendingTransfer();
        }
    }

    private void ReleasePendingTransfer()
    {
        lock (_gate)
        {
            _pendingTransfers--;
        }
    }

    private async ValueTask ReserveBytesAsync(long bytes, CancellationToken ct)
    {
        BytesWaiter waiter;
        lock (_gate)
        {
            if (CanStartNow(bytes))
            {
                _inFlightBytes += bytes;
                return;
            }

            waiter = new BytesWaiter
            {
                Bytes = bytes,
                Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            waiter.Node = _byteWaiters.AddLast(waiter);
            if (ct.CanBeCanceled)
            {
                waiter.CancellationRegistration = ct.Register(
                    static state =>
                    {
                        var (owner, pending, token) =
                            ((KeyedAsyncLock Owner, BytesWaiter Pending, CancellationToken Token))state!;
                        owner.CancelWaiter(pending, token);
                    },
                    (this, waiter, ct));
            }
        }

        try
        {
            await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            waiter.CancellationRegistration.Dispose();
        }
    }

    private void CancelWaiter(BytesWaiter waiter, CancellationToken token)
    {
        TaskCompletionSource<bool>? completion = null;
        lock (_gate)
        {
            if (waiter.Node is not null)
            {
                _byteWaiters.Remove(waiter.Node);
                waiter.Node = null;
                completion = waiter.Completion;
            }
        }

        completion?.TrySetCanceled(token);
    }

    private bool CanStartNow(long bytes)
    {
        if (bytes > _maxInFlightBytes)
            return _inFlightBytes == 0;

        return _inFlightBytes + bytes <= _maxInFlightBytes;
    }

    private void Release(string key, Entry entry, long bytesReserved)
    {
        if (bytesReserved > 0)
            ReleaseBytes(bytesReserved);

        entry.Semaphore.Release();
        ReleaseRef(key, entry);
    }

    private void ReleaseBytes(long bytes)
    {
        List<BytesWaiter>? toWake = null;
        lock (_gate)
        {
            _inFlightBytes = Math.Max(0, _inFlightBytes - bytes);

            while (_byteWaiters.Count > 0)
            {
                LinkedListNode<BytesWaiter>? selected = null;
                for (var node = _byteWaiters.First; node is not null; node = node.Next)
                {
                    if (CanStartNow(node.Value.Bytes))
                    {
                        selected = node;
                        break;
                    }
                }

                if (selected is null)
                    break;

                var waiter = selected.Value;
                _byteWaiters.Remove(selected);
                waiter.Node = null;
                _inFlightBytes += waiter.Bytes;
                (toWake ??= []).Add(waiter);
            }
        }

        if (toWake is null)
            return;

        foreach (var waiter in toWake)
        {
            waiter.CancellationRegistration.Dispose();
            waiter.Completion.TrySetResult(true);
        }
    }

    private void ReleaseRef(string key, Entry entry)
    {
        var dispose = false;
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

    public sealed class Releaser : IAsyncDisposable, IDisposable
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
            if (owner is null)
                return;

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

    internal int PendingTransfers
    {
        get
        {
            lock (_gate)
                return _pendingTransfers;
        }
    }

    internal long InFlightBytes
    {
        get
        {
            lock (_gate)
                return _inFlightBytes;
        }
    }

    public static long MegaBytes(long megabytes) => checked(megabytes * 1024L * 1024L);
}
