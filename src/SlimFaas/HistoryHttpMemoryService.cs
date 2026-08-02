using System.Collections.Concurrent;

namespace SlimFaas;

public class HistoryHttpMemoryService
{
    private readonly ConcurrentDictionary<string, long> _local = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeCalls = new(StringComparer.Ordinal);

    public long GetTicksLastCall(string functionName) =>
        _local.TryGetValue(functionName, out long value) ? value : 0L;

    public void SetTickLastCall(string functionName, long ticks) =>
        _local[functionName] = ticks;

    public void BeginActiveCall(string functionName)
    {
        _activeCalls.AddOrUpdate(functionName, 1, static (_, current) => current + 1);
        SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
    }

    public void EndActiveCall(string functionName)
    {
        SetTickLastCall(functionName, DateTime.UtcNow.Ticks);
        _activeCalls.AddOrUpdate(
            functionName,
            0,
            static (_, current) => current > 0 ? current - 1 : 0);
    }

    public bool HasActiveCalls(string functionName) =>
        _activeCalls.TryGetValue(functionName, out int count) && count > 0;

    public void RefreshActiveCall(string functionName, long ticks)
    {
        if (HasActiveCalls(functionName))
        {
            SetTickLastCall(functionName, ticks);
        }
    }
}
