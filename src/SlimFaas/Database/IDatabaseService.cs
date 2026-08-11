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

public readonly record struct QueueRunningReservation(string Id, string ReservedIp);

public sealed record QueueDispatchState(
    int Available,
    int Running,
    int WaitingForRetry,
    IReadOnlyList<QueueRunningReservation> RunningReservations)
{
    public static readonly QueueDispatchState Empty = new(0, 0, 0, []);
    public int TotalPending => Available + Running + WaitingForRetry;
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
    Task<KeyValueCommandResult> SetQueueMetadataAsync(string key, byte[] value) =>
        SetAsync(key, value);
    Task HashSetAsync(string key, IDictionary<string, byte[]> values, long? timeToLiveMilliseconds = null);
    Task HashSetDeleteAsync(string key, string dictionaryKey = "");
    Task<IDictionary<string, byte[]>> HashGetAllAsync(string key);
    Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation, string? newElementId = null);
    Task<IList<QueueData>?> ListRightPopAsync(string key, string transactionId, int count = 1, IList<string>? reservedIps = null);
    Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
    async Task<long> ListCountAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue) =>
        (await ListCountElementAsync(key, countTypes, maximum)).Count;
    async Task<QueueDispatchState> GetQueueDispatchStateAsync(string key)
    {
        IList<QueueData> available = await ListCountElementAsync(key, [CountType.Available]);
        IList<QueueData> running = await ListCountElementAsync(key, [CountType.Running]);
        IList<QueueData> retry = await ListCountElementAsync(key, [CountType.WaitingForRetry]);
        return new QueueDispatchState(
            available.Count,
            running.Count,
            retry.Count,
            running.Select(item => new QueueRunningReservation(item.Id, item.ReservedIp)).ToArray());
    }
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
}
