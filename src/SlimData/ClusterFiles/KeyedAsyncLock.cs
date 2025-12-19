using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

internal sealed class KeyedAsyncLock
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _locks = new(StringComparer.Ordinal);

    internal sealed class Entry
    {
        public int RefCount;
        public readonly SemaphoreSlim Semaphore = new(1, 1);
    }

    public async ValueTask<Releaser> AcquireAsync(string key, CancellationToken ct)
    {
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
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(this, key, entry);
        }
        catch
        {
            ReleaseRef(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void Release(string key, Entry entry)
    {
        entry.Semaphore.Release();
        ReleaseRef(key, entry, releaseSemaphore: true);
    }

    private void ReleaseRef(string key, Entry entry, bool releaseSemaphore)
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

    public readonly struct Releaser : IAsyncDisposable, IDisposable
    {
        private readonly KeyedAsyncLock _owner;
        private readonly string _key;
        private readonly Entry _entry;

        internal Releaser(KeyedAsyncLock owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose() => _owner.Release(_key, _entry);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
