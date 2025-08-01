﻿using SlimData;

namespace SlimFaas.Jobs;

public interface IJobQueue
{
    Task<string> EnqueueAsync(string key, byte[] message);
    Task<IList<QueueData>?> DequeueAsync(string key, int count = 1);
    Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus);
    public Task<IList<QueueData>> CountElementAsync(string key, IList<CountType> countTypes, int maximum = int.MaxValue);
}
