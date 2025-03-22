using SlimData;

namespace SlimFaas.Jobs;

public interface IJobQueue
{
    Task EnqueueAsync(string key, byte[] message);
    Task<IList<QueueData>?> DequeueAsync(string key, int count = 1);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
    public Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
}
