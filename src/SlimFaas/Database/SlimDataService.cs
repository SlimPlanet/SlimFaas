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

        _batcher.RegisterKind<ListLeftPushReq, string>(
            kind: "llp",
            batchHandler: BatchHandlerAsync,
            maxBatchSize: 512,
            sizeEstimatorBytes: r => r.SerializedPayload?.Length ?? 0
        );

        _batcher.RegisterKind<ListCallbackReq, bool>(
            kind: "lcb",
            batchHandler: BatchListCallbackHandlerAsync,
            maxBatchSize: 512,
            sizeEstimatorBytes: r => r.SerializedStatus?.Length ?? 0
        );
    }

    private ISupplier<SlimDataPayload> SimplePersistentState =>
        _serviceProvider.GetRequiredService<SlimPersistentState>();

    private static string Escape(string s) => Uri.EscapeDataString(s);

    private static long? ToExpireAtUtcTicks(long? ttlMs)
    {
        if (!ttlMs.HasValue) return null;

        var now = DateTime.UtcNow.Ticks;
        if (ttlMs.Value <= 0) return now; // expiré immédiatement

        // overflow-safe
        var add = ttlMs.Value * TimeSpan.TicksPerMillisecond; // <-- ms ici
        var expire = now + add;
        if (expire < now) return long.MaxValue; // overflow => "très loin"
        return expire;
    }

    private static string BuildTtlQuery(long? ttlMs)
        => ttlMs.HasValue ? $"&ttl={ttlMs.Value}" : string.Empty;

    private static string TtlKey(string key) => key + SlimDataInterpreter.TimeToLivePostfix;

    private static bool TryReadExpireAtFromKeyValues(SlimDataPayload data, string key, out long expireAtTicks)
    {
        expireAtTicks = 0;
        if (!data.KeyValues.TryGetValue(TtlKey(key), out var raw)) return false;
        var arr = raw.ToArray();
        if (arr.Length < sizeof(long)) return false;
        expireAtTicks = BitConverter.ToInt64(arr, 0);
        return true;
    }

    private static bool TryReadExpireAtFromHashsets(SlimDataPayload data, string key, out long expireAtTicks)
    {
        expireAtTicks = 0;
        if (!data.Hashsets.TryGetValue(key, out var meta)) return false;
        if (!meta.TryGetValue(SlimDataInterpreter.HashsetTtlField, out var raw)) return false;
        var arr = raw.ToArray();
        if (arr.Length < sizeof(long)) return false;
        expireAtTicks = BitConverter.ToInt64(arr, 0);
        return true;
    }

    public Task DeleteAsync(string key) =>
        Retry.DoAsync(() => DoDeleteAsync(key), _logger, _retryInterval);

    public async Task<byte[]?> GetAsync(string key) =>
        await Retry.DoAsync(() => DoGetAsync(key), _logger, _retryInterval);

    public async Task SetAsync(string key, byte[] value, long? timeToLiveMs = null) =>
        await Retry.DoAsync(() => DoSetAsync(key, value, timeToLiveMs), _logger, _retryInterval);

    public async Task HashSetAsync(string key, IDictionary<string, byte[]> values, long? timeToLiveMs = null) =>
        await Retry.DoAsync(() => DoHashSetAsync(key, values, timeToLiveMs), _logger, _retryInterval);

    public async Task<IDictionary<string, byte[]>> HashGetAllAsync(string key) =>
        await Retry.DoAsync(() => DoHashGetAllAsync(key), _logger, _retryInterval);

    public async Task<string> ListLeftPushAsync(string key, byte[] field, RetryInformation retryInformation)
    {
        return await Retry.DoAsync(async () =>
        {
            var input = new ListLeftPushInput(field, MemoryPackSerializer.Serialize(retryInformation));
            var payload = MemoryPackSerializer.Serialize(input);
            return await _batcher.EnqueueAsync<ListLeftPushReq, string>(
                "llp",
                new ListLeftPushReq(key, payload, field, retryInformation)
            ).ConfigureAwait(false);
        }, _logger, _retryInterval);
    }

    private async Task<IReadOnlyList<string>> BatchHandlerAsync(IReadOnlyList<ListLeftPushReq> batch, CancellationToken ct)
    {
        var endpoint = await GetAndWaitForLeader();

        var req = new ListLeftPushBatchRequest(
            batch.Select(b => new ListLeftPushBatchItem(b.Key, b.SerializedPayload)).ToArray()
        );
        Console.WriteLine("Push Item BatchHandlerAsync " + req.Items.Length);
        var bin = MemoryPackSerializer.Serialize(req);
        var isLeader = !_cluster.LeadershipToken.IsCancellationRequested;

        if (isLeader)
        {
            var result = await SlimData.Endpoints.ListLeftPushBatchCommand(_cluster, bin, new CancellationTokenSource());
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
            _ = await _batcher.EnqueueAsync<ListCallbackReq, bool>(
                "lcb",
                new ListCallbackReq(key, payload)
            ).ConfigureAwait(false);
        }, _logger, _retryInterval);

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
            var respLeader = await SlimData.Endpoints.ListCallbackBatchCommand(_cluster, bin, CancellationTokenSource.CreateLinkedTokenSource(ct));
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

        var data = SimplePersistentState.Invoke();

        if (TryReadExpireAtFromKeyValues(data, key, out var expireAt) && expireAt <= DateTime.UtcNow.Ticks)
        {
            return null;
        }

        return data.KeyValues.TryGetValue(key, out var value) ? value.ToArray() : null;
    }

    private async Task DoSetAsync(string key, byte[] value, long? ttlMs)
    {
        var endpoint = await GetAndWaitForLeader();
        var expireAtUtcTicks = ToExpireAtUtcTicks(ttlMs);

        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            var ps = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await SlimData.Endpoints.AddKeyValueCommand(ps, key, value, expireAtUtcTicks, _cluster, new CancellationTokenSource());
        }
        else
        {
            var uri = new Uri($"{endpoint}SlimData/AddKeyValue?key={Escape(key)}{BuildTtlQuery(ttlMs)}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(value) };
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
                throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    private async Task DoHashSetAsync(string key, IDictionary<string, byte[]> values, long? ttlMs)
    {
        var endpoint = await GetAndWaitForLeader();
        var expireAtUtcTicks = ToExpireAtUtcTicks(ttlMs);

        var hashset = new HashsetSet(key, values);
        var serialize = MemoryPackSerializer.Serialize(hashset);

        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            var ps = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await SlimData.Endpoints.AddHashSetCommand(ps, key, new Dictionary<string, byte[]>(values), expireAtUtcTicks, _cluster, new CancellationTokenSource());
        }
        else
        {
            var uri = new Uri($"{endpoint}SlimData/AddHashset?{(ttlMs.HasValue ? $"ttl={ttlMs.Value}" : "")}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(serialize) };
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
                throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    public async Task HashSetDeleteAsync(string key, string dictionaryKey = "") =>
        await Retry.DoAsync(() => DoHashSetDeleteAsync(key, dictionaryKey), _logger, _retryInterval);

    private async Task DoHashSetDeleteAsync(string key, string dictionaryKey = "")
    {
        var endpoint = await GetAndWaitForLeader();

        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            var ps = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await SlimData.Endpoints.DeleteHashSetCommand(ps, key, dictionaryKey, _cluster, new CancellationTokenSource());
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri($"{endpoint}SlimData/DeleteHashset?key={key}&dictionaryKey={dictionaryKey}"));
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
                throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    private async Task<IDictionary<string, byte[]>> DoHashGetAllAsync(string key)
    {
        await GetAndWaitForLeader();
        await MasterWaitForleaseToken();

        var data = SimplePersistentState.Invoke();

        if (TryReadExpireAtFromHashsets(data, key, out var expireAt) && expireAt <= DateTime.UtcNow.Ticks)
        {
            if (!_cluster.LeadershipToken.IsCancellationRequested)
            {
                var ps = _serviceProvider.GetRequiredService<SlimPersistentState>();
                await SlimData.Endpoints.DeleteHashSetCommand(ps, key, dictionaryKey: "", _cluster, new CancellationTokenSource());
            }
            return new Dictionary<string, byte[]>(0);
        }

        var dictionary = data.Hashsets.TryGetValue(key, out var value)
            ? value
            : ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;

        var result = new Dictionary<string, byte[]>(dictionary.Count);
        foreach (var kv in dictionary)
            result.Add(kv.Key, kv.Value.ToArray());

        return result;
    }

    private async Task DoDeleteAsync(string key)
    {
        var endpoint = await GetAndWaitForLeader();

        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            var ps = _serviceProvider.GetRequiredService<SlimPersistentState>();
            await SlimData.Endpoints.DeleteKeyValueCommand(ps, key, _cluster, new CancellationTokenSource());
        }
        else
        {
            var uri = new Uri($"{endpoint}SlimData/DeleteKeyValue?key={Escape(key)}");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
                throw new DataException("Error in calling SlimData HTTP Service");
        }
    }

    private async Task<IList<QueueData>?> DoListRightPopAsync(string key, string transactionId, int count = 1)
    {
        var endpoint = await GetAndWaitForLeader();
        if (!_cluster.LeadershipToken.IsCancellationRequested)
        {
            var ps = _serviceProvider.GetRequiredService<SlimPersistentState>();
            var result = await SlimData.Endpoints.ListRightPopCommand(ps, key, transactionId, count, _cluster, new CancellationTokenSource());
            return result.Items;
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{endpoint}SlimData/ListRightPop?transactionId={transactionId}"));
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(count.ToString()), key);

            request.Content = multipart;
            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await httpClient.SendAsync(request);
            if ((int)response.StatusCode >= 500)
                throw new DataException("Error in calling SlimData HTTP Service");

            var bin = await response.Content.ReadAsByteArrayAsync();
            var result = MemoryPackSerializer.Deserialize<ListItems>(bin);
            return result?.Items ?? new List<QueueData>();
        }
    }

    private async Task<IList<QueueData>> DoListCountElementAsync(string key, IList<CountType> countTypes, int maximum)
    {
        await GetAndWaitForLeader();

        var data = SimplePersistentState.Invoke();

        if (!data.Queues.TryGetValue(key, out var value) || value.IsDefaultOrEmpty || countTypes.Count == 0)
            return Array.Empty<QueueData>();

        var nowTicks = DateTime.UtcNow.Ticks;
        var result = new List<QueueElement>();

        if (countTypes.Contains(CountType.Available))
            result.AddRange(value.GetQueueAvailableElement(nowTicks, maximum));

        if (countTypes.Contains(CountType.Running))
            result.AddRange(value.GetQueueRunningElement(nowTicks));

        if (countTypes.Contains(CountType.WaitingForRetry))
            result.AddRange(value.GetQueueWaitingForRetryElement(nowTicks));

        return result.Select(qe => new QueueData(qe.Id, qe.Value.ToArray())).ToList();
    }

    private async Task MasterWaitForleaseToken()
    {
        var tryCount = 100;
        while (_cluster.TryGetLeaseToken(out var leaseToken) && leaseToken.IsCancellationRequested)
        {
            Console.WriteLine($"Master node is waiting for lease token {tryCount}");
            await Task.Delay(10);
            tryCount--;
            if (tryCount < 0)
                throw new Exception("Master node cannot have lease token");
        }
    }

    private async Task<EndPoint> GetAndWaitForLeader()
    {
        var timeWaited = TimeSpan.Zero;
        while (_cluster.Leader == null && timeWaited < _timeMaxToWaitForLeader)
        {
            await Task.Delay(500);
            timeWaited += TimeSpan.FromMilliseconds(500);
        }

        if (_cluster.Leader == null)
            throw new DataException("No leader found");

        return _cluster.Leader.EndPoint;
    }
}
#pragma warning restore CA2252
