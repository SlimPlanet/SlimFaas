using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SlimData;
using SlimData.Commands;

namespace SlimFaas;

public class DatabaseMockService : IDatabaseService
{
    private readonly ConcurrentDictionary<string, IDictionary<string, byte[]>> hashSet = new();
    private readonly ConcurrentDictionary<string, byte[]> keys = new();
    private readonly ConcurrentDictionary<string, long> expiresAt = new();
    private readonly ConcurrentDictionary<string, List<QueueData>> queue = new();

    private static long? ToExpireAtUtcTicks(long? ttlMs)
    {
        if (!ttlMs.HasValue) return null;
        var now = DateTime.UtcNow.Ticks;
        if (ttlMs.Value <= 0) return now;
        var add = ttlMs.Value * TimeSpan.TicksPerMillisecond;
        var expire = now + add;
        return expire < now ? long.MaxValue : expire;
    }

    private bool TryGetActiveValue(string key, out byte[] value)
    {
        value = Array.Empty<byte>();
        if (expiresAt.TryGetValue(key, out var expireAt) && expireAt <= DateTime.UtcNow.Ticks)
        {
            keys.TryRemove(key, out _);
            expiresAt.TryRemove(key, out _);
            return false;
        }

        return keys.TryGetValue(key, out value!);
    }

    public Task DeleteAsync(string key) => throw new NotImplementedException();

    Task IDatabaseService.HashSetDeleteAsync(string key, string dictionaryKey = "")
    {
        return Task.FromResult("");
    }
    public Task<byte[]?> GetAsync(string key)
    {
        if (TryGetActiveValue(key, out var value))
        {
            return Task.FromResult<byte[]?>(value);
        }

        return Task.FromResult<byte[]?>(null);
    }

    public Task<KeyValueCommandResult> SetAsync(
        string key,
        byte[]? value = null,
        long? timeToLiveMilliseconds = null,
        KeyValueOperation operation = KeyValueOperation.Set,
        long integerDelta = 0,
        decimal floatDelta = 0)
    {
        var result = new KeyValueCommandResult();
        var expireAt = ToExpireAtUtcTicks(timeToLiveMilliseconds);

        if (operation == KeyValueOperation.IncrementInteger)
        {
            var current = 0L;
            if (TryGetActiveValue(key, out var existing) &&
                !long.TryParse(Encoding.UTF8.GetString(existing), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out current))
            {
                result.SetError(KeyValueCommandStatus.InvalidNumber, "Value is not an integer.");
                return Task.FromResult(result);
            }

            long next;
            try
            {
                next = checked(current + integerDelta);
            }
            catch (OverflowException)
            {
                result.SetError(KeyValueCommandStatus.Overflow, "Integer increment overflow.");
                return Task.FromResult(result);
            }

            var bytes = Encoding.UTF8.GetBytes(next.ToString(CultureInfo.InvariantCulture));
            keys[key] = bytes;
            if (expireAt.HasValue)
                expiresAt[key] = expireAt.Value;
            result.SetApplied(bytes, integerValue: next);
            return Task.FromResult(result);
        }

        if (operation == KeyValueOperation.IncrementFloat)
        {
            var current = 0m;
            if (TryGetActiveValue(key, out var existing) &&
                !decimal.TryParse(
                    Encoding.UTF8.GetString(existing),
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                    CultureInfo.InvariantCulture,
                    out current))
            {
                result.SetError(KeyValueCommandStatus.InvalidNumber, "Value is not a decimal number.");
                return Task.FromResult(result);
            }

            decimal next;
            try
            {
                next = checked(current + floatDelta);
            }
            catch (OverflowException)
            {
                result.SetError(KeyValueCommandStatus.Overflow, "Decimal increment overflow.");
                return Task.FromResult(result);
            }

            var bytes = Encoding.UTF8.GetBytes(next.ToString("G29", CultureInfo.InvariantCulture));
            keys[key] = bytes;
            if (expireAt.HasValue)
                expiresAt[key] = expireAt.Value;
            result.SetApplied(bytes, decimalValue: next);
            return Task.FromResult(result);
        }

        var setValue = value ?? Array.Empty<byte>();
        keys[key] = setValue;
        if (expireAt.HasValue)
            expiresAt[key] = expireAt.Value;
        else
            expiresAt.TryRemove(key, out _);
        result.SetApplied(setValue);
        return Task.FromResult(result);
    }

    public Task HashSetAsync(string key, IDictionary<string, byte[]> values, long? timeToLiveSeconds = null)
    {
        if (hashSet.ContainsKey(key))
        {
            hashSet[key] = values;
        }
        else
        {
            hashSet.TryAdd(key, values);
        }

        return Task.CompletedTask;
    }

    public Task DeleteHashSetAsync(string key, string dictionaryKey = "") => throw new NotImplementedException();

    public Task<IDictionary<string, byte[]>> HashGetAllAsync(string key)
    {
        if (hashSet.TryGetValue(key, out IDictionary<string, byte[]>? value))
        {
            return Task.FromResult(value);
        }

        return Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>());
    }

    public Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation, string? newElementId = null)
    {
        List<QueueData> list;
        if (queue.TryGetValue(key, out List<QueueData>? value))
        {
            list = value;
        }
        else
        {
            list = new List<QueueData>();
            queue.TryAdd(key, list);
        }

        var elementId = string.IsNullOrWhiteSpace(newElementId) ? Guid.NewGuid().ToString() : newElementId;
        list.Add(new QueueData(elementId, field, 1, true, DateTime.UtcNow.Ticks, 30L * TimeSpan.TicksPerSecond));
        return Task.FromResult(elementId);
    }

    public Task<IList<QueueData>?> ListRightPopAsync(string key, string transactionId, int count = 1, IList<string>? reservedIps = null)
    {
        if (!queue.TryGetValue(key, out List<QueueData>? list))
        {
            return Task.FromResult<IList<QueueData>?>(new List<QueueData>());
        }

        var listToReturn = list.TakeLast(count).ToList();
        if (reservedIps is { Count: > 0 })
        {
            for (var i = 0; i < listToReturn.Count && i < reservedIps.Count; i++)
            {
                listToReturn[i] = listToReturn[i] with { ReservedIp = reservedIps[i] };
            }
        }
        if (listToReturn.Count > 0)
        {
            list.RemoveRange(listToReturn.Count - 1, listToReturn.Count);
            return Task.FromResult<IList<QueueData>?>(listToReturn);
        }

        return Task.FromResult<IList<QueueData>?>(new List<QueueData>());
    }

    public Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = Int32.MaxValue)
    {
        if (!queue.ContainsKey(key))
        {
            return Task.FromResult<IList<QueueData>>(new List<QueueData>());
        }

        var list = queue[key];

        return Task.FromResult<IList<QueueData>>(list);
    }

    public async Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus)
    {
        await Task.Delay(100);
    }
}
