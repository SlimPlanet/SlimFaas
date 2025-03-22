using SlimData;

namespace SlimFaas.Jobs;

public class JobQueue(IDatabaseService databaseService) : IJobQueue
{
    private const string KeyPrefix = "Job:";

    public async Task EnqueueAsync(string key, byte[] data)
    {
        RetryInformation retryInformation = new([2,4,8,16,32], 120, [500,502,503]);
        await databaseService.ListLeftPushAsync($"{KeyPrefix}{key}", data, retryInformation);
    }

    public async Task<IList<QueueData>?> DequeueAsync(string key, int count = 1)
    {
        var data = await databaseService.ListRightPopAsync($"{KeyPrefix}{key}", count);
        return data;
    }

    public async Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => await databaseService.ListCallbackAsync($"{KeyPrefix}{key}", queueItemStatus);

    public async Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue) => await databaseService.ListCountElementAsync($"{KeyPrefix}{key}", countTypes, maximum);

}
