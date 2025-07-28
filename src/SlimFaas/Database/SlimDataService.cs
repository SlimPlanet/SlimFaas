using System.Data;
using System.Net;
using DotNext;
using DotNext.Collections.Generic;
using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using SlimData;
using SlimData.Commands;



namespace SlimFaas.Database;
#pragma warning disable CA2252




public class SlimDataService(IHttpClientFactory httpClientFactory, IServiceProvider serviceProvider, IRaftCluster cluster, ILogger<SlimDataService> logger)
    : IDatabaseService
{
    public const string HttpClientName = "SlimDataHttpClient";
    private readonly IList<int> _retryInterval = new List<int> { 1, 1, 1 };
    private readonly TimeSpan _timeMaxToWaitForLeader = TimeSpan.FromMilliseconds(3000);

    private ISupplier<SlimDataPayload> SimplePersistentState =>
        serviceProvider.GetRequiredService<ISupplier<SlimDataPayload>>();

    public async Task<byte[]?> GetAsync(string key)
    {
        return await Retry.DoAsync(() => DoGetAsync(key), logger,  _retryInterval);
    }

    private async Task<byte[]?> DoGetAsync(string key)
    {
        await GetAndWaitForLeader();
        await MasterWaitForleaseToken();
        SlimDataPayload data = SimplePersistentState.Invoke();
        return data.KeyValues.TryGetValue(key, out ReadOnlyMemory<byte> value) ? value.ToArray() : null;
    }

    public async Task SetAsync(string key, byte[] value)
    {
        await Retry.DoAsync(() => DoSetAsync(key, value), logger, _retryInterval);
    }

    private async Task DoSetAsync(string key,  byte[] value)
    {
        EndPoint endpoint = await GetAndWaitForLeader();

        if (!cluster.LeadershipToken.IsCancellationRequested)
        {
            var simplePersistentState = serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.AddKeyValueCommand(simplePersistentState, key, value, cluster, new CancellationTokenSource());
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{endpoint}SlimData/AddKeyValue?key={key}"));
            request.Content = new ByteArrayContent(value);
            using var httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    public async Task HashSetAsync(string key, IDictionary<string, string> values)
    {
        await Retry.DoAsync(() => DoHashSetAsync(key, values), logger, _retryInterval);
    }

    private async Task DoHashSetAsync(string key, IDictionary<string, string> values)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        if (!cluster.LeadershipToken.IsCancellationRequested)
        {
            var simplePersistentState = serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.AddHashSetCommand(simplePersistentState, key, new Dictionary<string, string>(values), cluster, new CancellationTokenSource());
        }
        else
        {
            MultipartFormDataContent multipart = new();
            multipart.Add(new StringContent(key), "______key_____");
            foreach (KeyValuePair<string, string> value in values)
            {
                multipart.Add(new StringContent(value.Value), value.Key);
            }
            using var httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response =
                await httpClient.PostAsync(new Uri($"{endpoint}SlimData/AddHashset"), multipart);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    public async Task<IDictionary<string, string>> HashGetAllAsync(string key)
    {
        return await Retry.DoAsync(() =>DoHashGetAllAsync(key), logger, _retryInterval);
    }

    private async Task<IDictionary<string, string>> DoHashGetAllAsync(string key)
    {
        await GetAndWaitForLeader();
        await MasterWaitForleaseToken();

        SlimDataPayload data = SimplePersistentState.Invoke();
        return data.Hashsets.TryGetValue(key, out Dictionary<string, string>? value)
            ? (IDictionary<string, string>)value
            : new Dictionary<string, string>();
    }

    public async Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation)
    {
        return await Retry.DoAsync(() =>DoListLeftPushAsync(key, field, retryInformation), logger, _retryInterval);
    }
    private static int Count = 0;
    private async Task<string> DoListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation)
    {
        Count++;
        Console.WriteLine($"DoListLeftPush : {Count}");
        EndPoint endpoint = await GetAndWaitForLeader();
        ListLeftPushInput listLeftPushInput = new(field, MemoryPackSerializer.Serialize(retryInformation));
        byte[] serialize = MemoryPackSerializer.Serialize(listLeftPushInput);
        if (!cluster.LeadershipToken.IsCancellationRequested)
        {
            var simplePersistentState = serviceProvider.GetRequiredService<SlimPersistentState>();
            var elementId = await Endpoints.ListLeftPushCommand(simplePersistentState, key, serialize, cluster, new CancellationTokenSource());
            return elementId;
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListLeftPush?key={key}"));
            request.Content = new ByteArrayContent(serialize);
            using var httpClient = httpClientFactory.CreateClient(HttpClientName);
            HttpResponseMessage response = await httpClient.SendAsync(request);

            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
            var elementId = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(elementId))
            {
                throw new DataException("Received null or empty response from SlimData HTTP Service");
            }
            return elementId;
        }
    }

    public async Task<IList<QueueData>?> ListRightPopAsync(string key, int count = 1)
    {
        return await Retry.DoAsync(() => DoListRightPopAsync(key, count), logger, _retryInterval);
    }

    private async Task<IList<QueueData>?> DoListRightPopAsync(string key, int count = 1)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        if (!cluster.LeadershipToken.IsCancellationRequested)
        {
            var simplePersistentState = serviceProvider.GetRequiredService<SlimPersistentState>();
            var result = await Endpoints.ListRightPopCommand(simplePersistentState, key, count, cluster, new CancellationTokenSource());
            return result.Items;
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListRightPop"));
            MultipartFormDataContent multipart = new();
            multipart.Add(new StringContent(count.ToString()), key);

            request.Content = multipart;
            using var httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }

            var bin = await response.Content.ReadAsByteArrayAsync();
            ListItems? result = MemoryPackSerializer.Deserialize<ListItems>(bin);
            return result?.Items ?? new List<QueueData>();
        }
    }

    public Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes, int maximum = Int32.MaxValue)
    {
        return Retry.DoAsync(() => DoListCountElementAsync(key, countTypes, maximum), logger, _retryInterval);
    }

    private async Task<IList<QueueData>> DoListCountElementAsync(string key, IList<CountType> countTypes, int maximum)
    {
        await GetAndWaitForLeader();
        await MasterWaitForleaseToken();

        SlimDataPayload data = SimplePersistentState.Invoke();
        var result = new List<QueueElement>();

        if (!data.Queues.TryGetValue(key, out List<QueueElement>? value))
        {
            return new List<QueueData>(0);
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        if (countTypes.Count == 0)
        {
            return new List<QueueData>(0);
        }
        var availableElements = new List<QueueElement>();
        if (countTypes.Contains(CountType.Available))
        {
            availableElements = value.GetQueueAvailableElement(nowTicks, maximum);
        }

        var runningElements = new List<QueueElement>();
        if (countTypes.Contains(CountType.Running))
        {
            runningElements = value.GetQueueRunningElement(nowTicks);
        }

        var runningWaitingForRetryElements = new List<QueueElement>();

        if (countTypes.Contains(CountType.WaitingForRetry))
        {
            runningWaitingForRetryElements = value.GetQueueWaitingForRetryElement(nowTicks);
        }

        result.AddRange(availableElements);
        result.AddRange(runningElements);
        result.AddRange(runningWaitingForRetryElements);

        var finalResult = new List<QueueData>(result.Count);
        finalResult.AddRange(result.Select(queueElement => new QueueData(queueElement.Id, queueElement.Value.ToArray())));
        return finalResult;
    }

    public async Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus)
    {
        await Retry.DoAsync(() => DoListCallbackAsync(key, queueItemStatus), logger, _retryInterval);
    }

    private async Task DoListCallbackAsync(string key, ListQueueItemStatus queueItemStatus)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        if (!cluster.LeadershipToken.IsCancellationRequested)
        {
            var simplePersistentState = serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.ListCallbackCommandAsync(simplePersistentState, key, queueItemStatus, cluster, new CancellationTokenSource());
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListCallback?key={key}"));
            var field = MemoryPackSerializer.Serialize(queueItemStatus);
            request.Content = new ByteArrayContent(field);
            using var httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    private async Task MasterWaitForleaseToken()
    {
        while (cluster.TryGetLeaseToken(out var leaseToken) && leaseToken.IsCancellationRequested)
        {
            Console.WriteLine("Master node is waiting for lease token");
            await Task.Delay(10);
        }
    }

    private async Task<EndPoint> GetAndWaitForLeader()
    {
        TimeSpan timeWaited = TimeSpan.Zero;
        while (cluster.Leader == null && timeWaited < _timeMaxToWaitForLeader)
        {
            await Task.Delay(500);
            timeWaited += TimeSpan.FromMilliseconds(500);
        }

        if (cluster.Leader == null)
        {
            throw new DataException("No leader found");
        }

        return cluster.Leader.EndPoint;
    }
}
#pragma warning restore CA2252
