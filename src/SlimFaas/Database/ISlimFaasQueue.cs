using SlimData;

namespace SlimFaas.Database;

public interface ISlimFaasQueue
{
    Task<string> EnqueueAsync(string key, byte[] message, RetryInformation retryInformation);
    Task<IList<QueueData>?> DequeueAsync(string key, int count = 1);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
    public Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
}
