using SlimData;
using SlimData.Commands;
using SlimFaas.Database;

namespace SlimFaas;

public enum CountType
{
    Available,
    Running,
    WaitingForRetry
}


public interface IDatabaseService
{
    Task DeleteAsync(string key);
    Task<byte[]?> GetAsync(string key);
    Task<KeyValueCommandResult> SetAsync(
        string key,
        byte[]? value = null,
        long? timeToLiveMilliseconds = null,
        KeyValueOperation operation = KeyValueOperation.Set,
        long integerDelta = 0,
        decimal floatDelta = 0);
    Task HashSetAsync(string key, IDictionary<string, byte[]> values, long? timeToLiveMilliseconds = null);
    Task HashSetDeleteAsync(string key, string dictionaryKey = "");
    Task<IDictionary<string, byte[]>> HashGetAllAsync(string key);
    Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation, string? newElementId = null);
    Task<IList<QueueData>?> ListRightPopAsync(string key, string transactionId, int count = 1, IList<string>? reservedIps = null);
    Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
}
