using System.Collections.Immutable;
using System.Data;
using System.Net;
using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using SlimData;
using SlimData.Commands;

namespace SlimFaas.Database;

public readonly record struct ListLeftPushReq(string Key, byte[] SerializedPayload, byte[] Field, RetryInformation RetryInfo);

public readonly record struct ListCallbackReq(string Key, byte[] SerializedStatus);



#pragma warning disable CA2252
public class SlimDataService
    : IDatabaseService, IAsyncDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRaftCluster _cluster;
    private readonly ILogger<SlimDataService> _logger;
    public const string HttpClientName = "SlimDataHttpClient";
    private readonly IList<int> _retryInterval = new List<int> { 1, 1, 1 };
    private readonly TimeSpan _timeMaxToWaitForLeader = TimeSpan.FromMilliseconds(3000);
    private readonly MultiRateAdaptiveBatcher _batcher;

     public SlimDataService(
         IHttpClientFactory httpClientFactory,
         IServiceProvider serviceProvider,
         IRaftCluster cluster,
         ILogger<SlimDataService> logger)
     {
         _httpClientFactory = httpClientFactory;
         _serviceProvider = serviceProvider;
         _cluster = cluster;
         _logger = logger;
         _batcher = new MultiRateAdaptiveBatcher(
             idleStop: TimeSpan.FromSeconds(15),
             maxWaitPerTick: TimeSpan.FromSeconds(5)
         );

         // (optionnel) paliers spécifiques par kind
         var tiersLlp = new[]
         {
             new RateTier(20,  TimeSpan.FromMilliseconds(250)),
             new RateTier(300, TimeSpan.FromMilliseconds(500)),
         };
         var tiersLcb = new[]
         {
             new RateTier(50,  TimeSpan.FromMilliseconds(150)),
             new RateTier(500, TimeSpan.FromMilliseconds(400)),
         };

         // Kind "llp" : ListLeftPush -> string
         _batcher.RegisterKind<ListLeftPushReq, string>(
             kind: "llp",
             batchHandler: BatchHandlerAsync,
            // tiers: tiersLlp,
             maxBatchSize: 512,
             sizeEstimatorBytes: r => r.SerializedPayload?.Length ?? 0
         );

         // Kind "lcb" : ListCallback -> bool
         _batcher.RegisterKind<ListCallbackReq, bool>(
             kind: "lcb",
             batchHandler: BatchListCallbackHandlerAsync,
            // tiers: tiersLcb,
             maxBatchSize: 512,
             sizeEstimatorBytes: r => r.SerializedStatus?.Length ?? 0
         );
     }

    private ISupplier<SlimDataPayload> SimplePersistentState =>
        _serviceProvider.GetRequiredService<SlimPersistentState>();

    public Task DeleteAsync(string key) => throw new NotImplementedException();

    public async Task<byte[]?> GetAsync(string key) =>
        await Retry.DoAsync(() => DoGetAsync(key), _logger, _retryInterval);

    public async Task SetAsync(string key, byte[] value) =>
        await Retry.DoAsync(() => DoSetAsync(key, value), _logger, _retryInterval);

    public async Task HashSetAsync(string key, IDictionary<string, byte[]> values) =>
        await Retry.DoAsync(() => DoHashSetAsync(key, values), _logger, _retryInterval);

    public async Task<IDictionary<string, byte[]>> HashGetAllAsync(string key) =>
        await Retry.DoAsync(() => DoHashGetAllAsync(key), _logger, _retryInterval);

    // 4) Remplacer ListLeftPushAsync
    public async Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation)
    {
       return await Retry.DoAsync(async () =>
        {
            // pré-sérialisation identique à votre code
            var input = new ListLeftPushInput(field, MemoryPackSerializer.Serialize(retryInformation));
            var payload = MemoryPackSerializer.Serialize(input);
            return await _batcher.EnqueueAsync<ListLeftPushReq, string>(
                "llp",
                new ListLeftPushReq(key, payload, field, retryInformation)
            ).ConfigureAwait(false);
        }, _logger, _retryInterval);
    }


    // 6) Handler batch = leader/non-leader
    private async Task<IReadOnlyList<string>> BatchHandlerAsync(IReadOnlyList<ListLeftPushReq> batch, CancellationToken ct)
    {
        // Récupère endpoint + rôle
        var endpoint = await GetAndWaitForLeader();

        var req = new ListLeftPushBatchRequest(
            batch.Select(b => new ListLeftPushBatchItem(b.Key, b.SerializedPayload)).ToArray()
        );
        Console.WriteLine("Push Item BatchHandlerAsync " + req.Items.Length) ;
        var bin = MemoryPackSerializer.Serialize(req);
        var isLeader = !_cluster.LeadershipToken.IsCancellationRequested;

        if (isLeader)
        {
            var result = await Endpoints.ListLeftPushBatchCommand(_cluster, bin, new CancellationTokenSource());
            return result.ElementIds;
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListLeftPushBatch"))
        {
            Content = new ByteArrayContent(bin)
        };
        using var response = await httpClient.SendAsync(httpRequest, ct);
        if ((int)response.StatusCode >= 500)
            throw new DataException("Error in calling SlimData HTTP Service (batch)");

        var respBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var resp = MemoryPackSerializer.Deserialize<ListLeftPushBatchResponse>(respBytes)
                   ?? throw new DataException("Null batch response");

        if (resp.ElementIds.Length != batch.Count)
            throw new DataException("Batch response count mismatch");

        return resp.ElementIds;
    }

    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync();
    }

    public async Task<IList<QueueData>?> ListRightPopAsync(string key, string transactionId, int count = 1)
    {
        return await Retry.DoAsync(() => DoListRightPopAsync(key, transactionId, count), _logger, _retryInterval);
    }

    public Task<IList<QueueData>> ListCountElementAsync(string key, IList<CountType> countTypes,
        int maximum = int.MaxValue) =>
        Retry.DoAsync(() => DoListCountElementAsync(key, countTypes, maximum), _logger, _retryInterval);

    public async Task ListCallbackAsync(string key, ListQueueItemStatus queueItemStatus) =>
        await Retry.DoAsync(async () =>
        {
            var payload = MemoryPackSerializer.Serialize(queueItemStatus);
            // on enfile dans le batcher ; le bool renvoyé n’a pas d’importance ici
            _ = await _batcher.EnqueueAsync<ListCallbackReq, bool>(
                "lcb",
                new ListCallbackReq(key, payload)
            ).ConfigureAwait(false);
        }, _logger, _retryInterval);


    // Batch = leader / non leader (comme pour ListLeftPushBatch)
    private async Task<IReadOnlyList<bool>> BatchListCallbackHandlerAsync(
        IReadOnlyList<ListCallbackReq> batch, CancellationToken ct)
    {
        var endpoint = await GetAndWaitForLeader();

        var req = new ListCallbackBatchRequest(
            batch.Select(b => new ListCallbackBatchItem(b.Key, b.SerializedStatus)).ToArray()
        );
        var bin = MemoryPackSerializer.Serialize(req);
        var isLeader = !_cluster.LeadershipToken.IsCancellationRequested;

        if (isLeader)
        {
            // Commande locale (à implémenter côté serveur, cf. section 3)
            var respLeader = await Endpoints.ListCallbackBatchCommand(_cluster, bin, CancellationTokenSource.CreateLinkedTokenSource(ct));
            if (respLeader.Acks.Length != batch.Count)
                throw new DataException("Batch response count mismatch");
            return respLeader.Acks;
        }

        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListCallbackBatch"))
        {
            Content = new ByteArrayContent(bin)
        };
        using var response = await httpClient.SendAsync(httpRequest, ct);
        if ((int)response.StatusCode >= 500)
            throw new DataException("Error in calling SlimData HTTP Service (callback batch)");

        var respBytes = await response.Content.ReadAsByteArrayAsync(ct);
        var resp = MemoryPackSerializer.Deserialize<ListCallbackBatchResponse>(respBytes);
        if (resp.Acks.Length != batch.Count)
            throw new DataException("Batch response count mismatch");

        return resp.Acks;
    }


    private async Task<byte[]?> DoGetAsync(string key)
    {
        await GetAndWaitForLeader();
        await MasterWaitForleaseToken();
        SlimDataPayload data = SimplePersistentState.Invoke();
        return data.KeyValues.TryGetValue(key, out ReadOnlyMemory<byte> value) ? value.ToArray() : null;
    }

    private async Task DoSetAsync(string key, byte[] value)
    {
        EndPoint endpoint = await GetAndWaitForLeader();

        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            SlimPersistentState simplePersistentState = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.AddKeyValueCommand(simplePersistentState, key, value, _cluster,
                new CancellationTokenSource());
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                new Uri($"{endpoint}SlimData/AddKeyValue?key={key}"));
            request.Content = new ByteArrayContent(value);
            using HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    private async Task DoHashSetAsync(string key, IDictionary<string, byte[]> values)
    {
        EndPoint endpoint = await GetAndWaitForLeader();

        HashsetSet hashset = new(key, values);
        byte[] serialize = MemoryPackSerializer.Serialize(hashset);
        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            SlimPersistentState simplePersistentState = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.AddHashSetCommand(simplePersistentState, key, new Dictionary<string, byte[]>(values),
                _cluster, new CancellationTokenSource());
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                new Uri($"{endpoint}SlimData/AddHashset"));
            request.Content = new ByteArrayContent(serialize);
            using HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);

            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    public async Task HashSetDeleteAsync(string key, string dictionaryKey = "") =>
        await Retry.DoAsync(() => DoHashSetDeleteAsync(key, dictionaryKey), _logger, _retryInterval);

    private async Task DoHashSetDeleteAsync(string key, string dictionaryKey = "")
    {
        EndPoint endpoint = await GetAndWaitForLeader();

        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            SlimPersistentState simplePersistentState = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.DeleteHashSetCommand(simplePersistentState, key, dictionaryKey,
                _cluster, new CancellationTokenSource());
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                new Uri($"{endpoint}SlimData/DeleteHashset?key={key}&dictionaryKey={dictionaryKey}"));
            using HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    private async Task<IDictionary<string, byte[]>> DoHashGetAllAsync(string key)
    {
        await GetAndWaitForLeader();
        await MasterWaitForleaseToken();

        SlimDataPayload data = SimplePersistentState.Invoke();

        var dictionary = data.Hashsets.TryGetValue(key, out ImmutableDictionary<string, ReadOnlyMemory<byte>>? value)
            ? value
            : ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;

        Dictionary<string, byte[]> result = new(dictionary.Count);
        foreach (KeyValuePair<string, ReadOnlyMemory<byte>> readOnlyMemory in dictionary)
        {
            result.Add(readOnlyMemory.Key, readOnlyMemory.Value.ToArray());
        }

        return result;
    }

    private async Task<string> DoListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        ListLeftPushInput listLeftPushInput = new(field, MemoryPackSerializer.Serialize(retryInformation));
        byte[] serialize = MemoryPackSerializer.Serialize(listLeftPushInput);
        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            SlimPersistentState simplePersistentState = _serviceProvider.GetRequiredService<SlimPersistentState>();
            string elementId = await Endpoints.ListLeftPushCommand(simplePersistentState, key, serialize, _cluster,
                new CancellationTokenSource());
            return elementId;
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                new Uri($"{endpoint}SlimData/ListLeftPush?key={key}"));
            request.Content = new ByteArrayContent(serialize);
            using HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);

            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }

            string elementId = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(elementId))
            {
                throw new DataException("Received null or empty response from SlimData HTTP Service");
            }

            return elementId;
        }
    }

    private async Task<IList<QueueData>?> DoListRightPopAsync(string key, string transactionId, int count = 1)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            SlimPersistentState simplePersistentState = _serviceProvider.GetRequiredService<SlimPersistentState>();
            ListItems result = await Endpoints.ListRightPopCommand(simplePersistentState, key, transactionId, count,
                _cluster, new CancellationTokenSource());
            return result.Items;
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post, new Uri(
                $"{endpoint}SlimData/ListRightPop?transactionId={transactionId}"));
            MultipartFormDataContent multipart = new();
            multipart.Add(new StringContent(count.ToString()), key);

            request.Content = multipart;
            using HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }

            byte[] bin = await response.Content.ReadAsByteArrayAsync();
            ListItems? result = MemoryPackSerializer.Deserialize<ListItems>(bin);
            return result?.Items ?? new List<QueueData>();
        }
    }

    private async Task<IList<QueueData>> DoListCountElementAsync(string key, IList<CountType> countTypes, int maximum)
    {
        await GetAndWaitForLeader();

        SlimDataPayload data = SimplePersistentState.Invoke();
        List<QueueElement> result = new();

        if (!data.Queues.TryGetValue(key, out ImmutableArray<QueueElement> value) ||
            value.IsDefaultOrEmpty ||
            countTypes.Count == 0)
        {
            return Array.Empty<QueueData>();
        }

        long nowTicks = DateTime.UtcNow.Ticks;
        if (countTypes.Count == 0)
        {
            return new List<QueueData>(0);
        }

        List<QueueElement> availableElements = new();
        if (countTypes.Contains(CountType.Available))
        {
            availableElements = value.GetQueueAvailableElement(nowTicks, maximum).ToList();
        }

        List<QueueElement> runningElements = new();
        if (countTypes.Contains(CountType.Running))
        {
            runningElements = value.GetQueueRunningElement(nowTicks).ToList();
        }

        List<QueueElement> runningWaitingForRetryElements = new();

        if (countTypes.Contains(CountType.WaitingForRetry))
        {
            runningWaitingForRetryElements = value.GetQueueWaitingForRetryElement(nowTicks).ToList();
        }

        result.AddRange(availableElements);
        result.AddRange(runningElements);
        result.AddRange(runningWaitingForRetryElements);

        List<QueueData> finalResult = new(result.Count);
        finalResult.AddRange(
            result.Select(queueElement => new QueueData(queueElement.Id, queueElement.Value.ToArray())));
        return finalResult;
    }

    private async Task DoListCallbackAsync(string key, ListQueueItemStatus queueItemStatus)
    {
        EndPoint endpoint = await GetAndWaitForLeader();
        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            SlimPersistentState simplePersistentState = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await Endpoints.ListCallbackCommandAsync(simplePersistentState, key, queueItemStatus, _cluster,
                new CancellationTokenSource());
        }
        else
        {
            using HttpRequestMessage request = new(HttpMethod.Post,
                new Uri($"{endpoint}SlimData/ListCallback?key={key}"));
            byte[] field = MemoryPackSerializer.Serialize(queueItemStatus);
            request.Content = new ByteArrayContent(field);
            using HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
            {
                throw new DataException("Error in calling SlimData HTTP Service");
            }
        }
    }

    private async Task MasterWaitForleaseToken()
    {
        int tryCount = 100;
        while (_cluster.TryGetLeaseToken(out CancellationToken leaseToken) && leaseToken.IsCancellationRequested)
        {
            Console.WriteLine($"Master node is waiting for lease token {tryCount}");
            await Task.Delay(10);
            tryCount--;
            if (tryCount < 0)
            {
                throw new Exception("Master node cannot have lease token");
            }
        }
    }

    private async Task<EndPoint> GetAndWaitForLeader()
    {
        TimeSpan timeWaited = TimeSpan.Zero;
        while (_cluster.Leader == null && timeWaited < _timeMaxToWaitForLeader)
        {
            await Task.Delay(500);
            timeWaited += TimeSpan.FromMilliseconds(500);
        }

        if (_cluster.Leader == null)
        {
            throw new DataException("No leader found");
        }

        return _cluster.Leader.EndPoint;
    }
}
#pragma warning restore CA2252
