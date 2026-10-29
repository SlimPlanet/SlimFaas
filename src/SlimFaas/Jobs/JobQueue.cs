using SlimData;

namespace SlimFaas.Jobs;

public class JobQueue(IDatabaseService databaseService) : IJobQueue
{
    private const string KeyPrefix = "Job:";

    /// <summary>
    /// Queue name of a job configuration in network activity. The prefix keeps a job
    /// queue apart from the queue of a function with the same name.
    /// </summary>
    public static string ActivityQueueName(string configurationName) => $"job:{configurationName}";

    public async Task<string> EnqueueAsync(string key, byte[] message)
    {
        RetryInformation retryInformation = new([2,4,8,16,32], 120, [500,502,503]);
        return await databaseService.ListLeftPushAsync($"{KeyPrefix}{key}", message, retryInformation);
    }

    public async Task<IList<QueueData>?> DequeueAsync(string key, int count = 1)
    {
        var data = await databaseService.ListRightPopAsync($"{KeyPrefix}{key}", Guid.NewGuid().ToString(), count);
        return data;
    }

    public async Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) => await databaseService.ListCallbackAsync($"{KeyPrefix}{key}", queueItemStatus);

    public async Task<IList<QueueData>> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue) => await databaseService.ListCountElementAsync($"{KeyPrefix}{key}", countTypes, maximum);

    public async Task<long> CountAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue) => await databaseService.ListCountAsync($"{KeyPrefix}{key}", countTypes, maximum);

}
