using SlimData;

namespace SlimFaas.Database;

internal sealed class SlimDataBatchCapacityLimiter(int maxItems, long maxBytes)
{
    private readonly object _gate = new();
    private int _items;
    private long _bytes;

    public void Reserve(string kind, int bytes)
    {
        lock (_gate)
        {
            if (_items >= maxItems || bytes > maxBytes - _bytes)
                throw new BatchQueueFullException(kind);

            _items++;
            _bytes += bytes;
        }
    }

    public void Release(int bytes)
    {
        lock (_gate)
        {
            _items--;
            _bytes -= bytes;
        }
    }
}
