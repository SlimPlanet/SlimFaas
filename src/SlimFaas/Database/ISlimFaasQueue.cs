using SlimData;

namespace SlimFaas.Database;

public interface ISlimFaasQueue
{
    Task<string> EnqueueAsync(string key, byte[] message, RetryInformation retryInformation, string? newElementId = null);
    Task<IList<QueueData>?> DequeueAsync(string key, int count = 1, IList<string>? reservedIps = null);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
    public Task<long> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
    public Task<IList<QueueData>> ListElementsAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
    async Task<QueueDispatchState> GetDispatchStateAsync(string key)
    {
        IList<QueueData> available = await ListElementsAsync(key, [CountType.Available]);
        IList<QueueData> running = await ListElementsAsync(key, [CountType.Running]);
        IList<QueueData> retry = await ListElementsAsync(key, [CountType.WaitingForRetry]);
        return new QueueDispatchState(
            available.Count,
            running.Count,
            retry.Count,
            running.Select(item => new QueueRunningReservation(item.Id, item.ReservedIp)).ToArray());
    }
}
