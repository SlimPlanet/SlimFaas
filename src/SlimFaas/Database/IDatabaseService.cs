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
    Task<byte[]?> GetAsync(string key);
    Task SetAsync(string key,  byte[] value);
    Task HashSetAsync(string key, IDictionary<string, string> values);
    Task<IDictionary<string, string>> HashGetAllAsync(string key);
    Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation);
    Task<IList<QueueData>?> ListRightPopAsync(string key, int count = 1);
    Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
}
