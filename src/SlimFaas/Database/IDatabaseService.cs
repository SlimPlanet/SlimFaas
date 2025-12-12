using SlimData;
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
    Task SetAsync(string key,  byte[] value, long? timeToLiveMilliseconds = null);
    Task HashSetAsync(string key, IDictionary<string, byte[]> values, long? timeToLiveMilliseconds = null);
    Task HashSetDeleteAsync(string key, string dictionaryKey = "");
    Task<IDictionary<string, byte[]>> HashGetAllAsync(string key);
    Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation);
    Task<IList<QueueData>?> ListRightPopAsync(string key, string transactionId, int count = 1);
    Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
}
