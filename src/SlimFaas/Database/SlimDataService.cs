using System.Collections.Immutable;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Text;
using DotNext;
using DotNext.Net.Cluster.Consensus.Raft;
using MemoryPack;
using SlimData;
using SlimData.Commands;

namespace SlimFaas.Database;

#pragma warning disable CA2252
public sealed class SlimDataService : IDatabaseService, IAsyncDisposable
{
    private const string UnifiedBatchKind = "commands";
    private const string UnifiedBatchResource = "SlimData/CommandBatch";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IRaftCluster _cluster;
    private readonly ISlimDataProtocolCompatibility _protocolCompatibility;
    private readonly ILogger<SlimDataService> _logger;
    private readonly TimeSpan _timeMaxToWaitForLeader = TimeSpan.FromMilliseconds(3000);
    private readonly MultiRateAdaptiveBatcher _batcher;
    private readonly string _producerId;
    private readonly string _generationId = Guid.NewGuid().ToString("N");
    private long _lastSuccessfulSequence;

    public const string HttpClientName = "SlimDataHttpClient";

    public SlimDataService(
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider,
        IRaftCluster cluster,
        ISlimDataProtocolCompatibility protocolCompatibility,
        SlimDataInfo slimDataInfo,
        ILogger<SlimDataService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
        _cluster = cluster;
        _protocolCompatibility = protocolCompatibility;
        _logger = logger;
        _producerId = $"{Environment.MachineName}:{slimDataInfo.Port}";
        _batcher = new MultiRateAdaptiveBatcher(
            idleStop: TimeSpan.FromSeconds(15),
            maxWaitPerTick: TimeSpan.FromSeconds(5));
        _batcher.RegisterKind<SlimDataBatchOperation, SlimDataBatchOperationResult>(
            kind: UnifiedBatchKind,
            batchHandler: BatchMutationHandlerAsync,
            tiers:
            [
                new RateTier(0, TimeSpan.FromMilliseconds(225)),
                new RateTier(4_000, TimeSpan.FromMilliseconds(100)),
            ],
            maxBatchSize: 512,
            maxQueueLength: 4096,
            maxQueueBytes: 64L * 1024 * 1024,
            maxBatchBytes: 15 * 1024 * 1024,
            sizeEstimatorBytes: EstimateOperationBytes,
            coalesceWindow: TimeSpan.FromMilliseconds(3));
    }

    private ISupplier<SlimDataPayload> SimplePersistentState =>
        _serviceProvider.GetRequiredService<SlimPersistentState>();

    public IReadOnlyList<AdaptiveBatchQueueStatistics> BatchQueueStatistics
        => _batcher.GetQueueStatistics();

    private static long? ToExpireAtUtcTicks(long? ttlMs)
    {
        if (!ttlMs.HasValue)
            return null;

        var now = DateTime.UtcNow.Ticks;
        if (ttlMs.Value <= 0)
            return now;

        try
        {
            return checked(now + checked(ttlMs.Value * TimeSpan.TicksPerMillisecond));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static string TtlKey(string key) => key + SlimDataInterpreter.TimeToLivePostfix;

    private static int EstimateOperationBytes(SlimDataBatchOperation operation)
    {
        long result = 256L +
                      Encoding.UTF8.GetByteCount(operation.Key ?? string.Empty) +
                      Encoding.UTF8.GetByteCount(operation.RequestId ?? string.Empty) +
                      (operation.Value?.Length ?? 0);
        if (operation.HashValues is not null)
        {
            foreach (var (field, value) in operation.HashValues)
                result += Encoding.UTF8.GetByteCount(field) + (value?.Length ?? 0) + 16L;
        }

        result += (operation.Retries?.Length ?? 0) * sizeof(int);
        result += (operation.HttpStatusRetries?.Length ?? 0) * sizeof(int);
        result += (operation.ReservedIps?.Sum(Encoding.UTF8.GetByteCount) ?? 0);
        result += (operation.CallbackItems?.Length ?? 0) * 64L;
        return checked((int)Math.Min(int.MaxValue, result));
    }

    private static SlimDataBatchOperation NewOperation(
        SlimDataBatchOperationKind kind,
        string key)
        => new()
        {
            Kind = kind,
            RequestId = Guid.NewGuid().ToString("N"),
            Key = key,
            NowTicks = DateTime.UtcNow.Ticks
        };

    public async Task DeleteAsync(string key)
    {
        _ = await EnqueueMutationAsync(
                NewOperation(SlimDataBatchOperationKind.DeleteKeyValue, key))
            .ConfigureAwait(false);
    }

    public Task<byte[]?> GetAsync(string key) =>
        Retry.DoAsync(() => DoGetAsync(key), _logger, [1, 1, 1]);

    public async Task<KeyValueCommandResult> SetAsync(
        string key,
        byte[]? value = null,
        long? timeToLiveMs = null,
        KeyValueOperation operation = KeyValueOperation.Set,
        long integerDelta = 0,
        decimal floatDelta = 0)
    {
        var mutation = NewOperation(SlimDataBatchOperationKind.KeyValue, key);
        mutation.Value = value ?? [];
        mutation.ExpireAtUtcTicks = ToExpireAtUtcTicks(timeToLiveMs);
        mutation.KeyValueOperation = operation;
        mutation.IntegerDelta = integerDelta;
        mutation.FloatDelta = floatDelta;

        var response = await EnqueueMutationAsync(mutation).ConfigureAwait(false);
        return response.KeyValueResult
               ?? throw new DataException("The unified SlimData batch did not return a key/value result.");
    }

    public async Task HashSetAsync(
        string key,
        IDictionary<string, byte[]> values,
        long? timeToLiveMs = null)
    {
        var mutation = NewOperation(SlimDataBatchOperationKind.AddHashSet, key);
        mutation.HashValues = new Dictionary<string, byte[]>(values);
        mutation.ExpireAtUtcTicks = ToExpireAtUtcTicks(timeToLiveMs);
        _ = await EnqueueMutationAsync(mutation).ConfigureAwait(false);
    }

    public async Task HashSetDeleteAsync(string key, string dictionaryKey = "")
    {
        var mutation = NewOperation(SlimDataBatchOperationKind.DeleteHashSet, key);
        mutation.DictionaryKey = dictionaryKey;
        _ = await EnqueueMutationAsync(mutation).ConfigureAwait(false);
    }

    public Task<IDictionary<string, byte[]>> HashGetAllAsync(string key) =>
        Retry.DoAsync(() => DoHashGetAllAsync(key), _logger, [1, 1, 1]);

    public async Task<string> ListLeftPushAsync(
        string key,
        byte[] field,
        RetryInformation retryInformation,
        string? newElementId = null)
    {
        var mutation = NewOperation(SlimDataBatchOperationKind.ListLeftPush, key);
        mutation.Value = field;
        mutation.ElementId = string.IsNullOrWhiteSpace(newElementId)
            ? Guid.NewGuid().ToString()
            : newElementId;
        mutation.RetryTimeoutSeconds = retryInformation.RetryTimeoutSeconds;
        mutation.Retries = retryInformation.Retries?.ToArray() ?? [];
        mutation.HttpStatusRetries = retryInformation.HttpStatusRetries?.ToArray() ?? [];

        var response = await EnqueueMutationAsync(mutation).ConfigureAwait(false);
        return response.ElementId
               ?? throw new DataException("The unified SlimData batch did not return a queue element ID.");
    }

    public async Task<IList<QueueData>?> ListRightPopAsync(
        string key,
        string transactionId,
        int count = 1,
        IList<string>? reservedIps = null)
    {
        var mutation = NewOperation(SlimDataBatchOperationKind.ListRightPop, key);
        mutation.TransactionId = transactionId;
        mutation.Count = count;
        mutation.ReservedIps = reservedIps?.ToArray() ?? [];
        var response = await EnqueueMutationAsync(mutation).ConfigureAwait(false);
        return response.QueueItems?.ToList() ?? [];
    }

    public Task<IList<QueueData>> ListCountElementAsync(
        string key,
        IList<CountType> countTypes,
        int maximum = int.MaxValue) =>
        Retry.DoAsync(
            () => DoListCountElementAsync(key, countTypes, maximum),
            _logger,
            [1, 1, 1]);

    public async Task ListCallbackAsync(
        string key,
        ListQueueItemStatus queueItemStatus)
    {
        var mutation = NewOperation(SlimDataBatchOperationKind.ListCallback, key);
        mutation.CallbackItems = queueItemStatus.Items?.ToArray() ?? [];
        _ = await EnqueueMutationAsync(mutation).ConfigureAwait(false);
    }

    private Task<SlimDataBatchOperationResult> EnqueueMutationAsync(
        SlimDataBatchOperation operation)
        => _batcher.EnqueueAsync<SlimDataBatchOperation, SlimDataBatchOperationResult>(
            UnifiedBatchKind,
            operation);

    private async Task<IReadOnlyList<SlimDataBatchOperationResult>> BatchMutationHandlerAsync(
        IReadOnlyList<SlimDataBatchOperation> batch,
        CancellationToken cancellationToken)
    {
        var sequence = _lastSuccessfulSequence + 1;
        var request = new SlimDataCommandBatchRequest
        {
            ProducerId = _producerId,
            GenerationId = _generationId,
            Sequence = sequence,
            RequestId = Guid.NewGuid().ToString("N"),
            Operations = batch.ToArray()
        };
        var payload = MemoryPackSerializer.Serialize(request);
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await SendCommandBatchAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                if (response.SequenceGap)
                {
                    throw new InvalidDataException(
                        response.ErrorMessage ??
                        $"SlimData producer sequence gap: expected {response.ExpectedSequence}.");
                }
                if (response.Sequence != sequence ||
                    !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
                    response.Results.Length != batch.Count)
                {
                    throw new InvalidDataException("The unified SlimData batch response does not match its request.");
                }

                for (var i = 0; i < batch.Count; i++)
                {
                    if (!string.Equals(
                            response.Results[i].RequestId,
                            batch[i].RequestId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The unified SlimData operation response order does not match its request.");
                    }
                }

                _lastSuccessfulSequence = sequence;
                return response.Results;
            }
            catch (Exception ex) when (IsRetryableBatchFailure(ex, cancellationToken))
            {
                attempt++;
                _logger.LogWarning(
                    ex,
                    "Retrying the same ordered SlimData batch. Producer={ProducerId}, Sequence={Sequence}, Attempt={Attempt}",
                    _producerId,
                    sequence,
                    attempt);
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<SlimDataCommandBatchResponse> SendCommandBatchAsync(
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var endpoint = await GetAndWaitForLeader(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{endpoint}{UnifiedBatchResource}"))
        {
            Content = new ByteArrayContent(payload)
        };
        using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            throw new InvalidDataException(
                bytes.Length == 0
                    ? "The unified SlimData endpoint rejected the command batch."
                    : Encoding.UTF8.GetString(bytes));
        }

        if (response.StatusCode == HttpStatusCode.Conflict && bytes.Length > 0)
        {
            return MemoryPackSerializer.Deserialize<SlimDataCommandBatchResponse>(bytes)
                   ?? throw new DataException("The unified SlimData endpoint returned an empty conflict response.");
        }

        ThrowIfSlimDataUnavailable(response);
        if (!response.IsSuccessStatusCode)
            throw new DataException($"The unified SlimData endpoint returned HTTP {(int)response.StatusCode}.");

        return MemoryPackSerializer.Deserialize<SlimDataCommandBatchResponse>(bytes)
               ?? throw new DataException("The unified SlimData endpoint returned a null response.");
    }

    private static bool IsRetryableBatchFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;
        return exception is not InvalidDataException and
            not BatchItemTooLargeException and
            not BatchQueueFullException;
    }

    private async Task<byte[]?> DoGetAsync(string key)
    {
        await GetAndWaitForLeader().ConfigureAwait(false);
        await WaitForLocalApplyAsync().ConfigureAwait(false);
        await MasterWaitForLeaseToken().ConfigureAwait(false);

        var data = SimplePersistentState.Invoke();
        if (TryReadExpireAtFromKeyValues(data, key, out var expireAt) &&
            expireAt <= DateTime.UtcNow.Ticks)
        {
            return null;
        }

        return data.KeyValues.TryGetValue(key, out var value)
            ? value.ToArray()
            : null;
    }

    private async Task<IDictionary<string, byte[]>> DoHashGetAllAsync(string key)
    {
        await GetAndWaitForLeader().ConfigureAwait(false);
        await WaitForLocalApplyAsync().ConfigureAwait(false);
        await MasterWaitForLeaseToken().ConfigureAwait(false);

        var data = SimplePersistentState.Invoke();
        if (TryReadExpireAtFromHashsets(data, key, out var expireAt) &&
            expireAt <= DateTime.UtcNow.Ticks)
        {
            await HashSetDeleteAsync(key).ConfigureAwait(false);
            return new Dictionary<string, byte[]>(0);
        }

        var dictionary = data.Hashsets.TryGetValue(key, out var value)
            ? value
            : ImmutableDictionary<string, ReadOnlyMemory<byte>>.Empty;
        var result = new Dictionary<string, byte[]>(dictionary.Count);
        foreach (var (field, bytes) in dictionary)
        {
            if (!string.Equals(field, SlimDataInterpreter.HashsetTtlField, StringComparison.Ordinal))
                result.Add(field, bytes.ToArray());
        }
        return result;
    }

    private async Task<IList<QueueData>> DoListCountElementAsync(
        string key,
        IList<CountType> countTypes,
        int maximum)
    {
        await GetAndWaitForLeader().ConfigureAwait(false);
        await WaitForLocalApplyAsync().ConfigureAwait(false);

        var data = SimplePersistentState.Invoke();
        if (!data.Queues.TryGetValue(key, out var value) ||
            value.IsDefaultOrEmpty ||
            countTypes.Count == 0)
        {
            return [];
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        var result = new List<QueueElement>();
        if (countTypes.Contains(CountType.Available))
            result.AddRange(value.GetQueueAvailableElement(nowTicks, maximum));
        if (countTypes.Contains(CountType.Running))
            result.AddRange(value.GetQueueRunningElement(nowTicks));
        if (countTypes.Contains(CountType.WaitingForRetry))
            result.AddRange(value.GetQueueWaitingForRetryElement(nowTicks));

        return result
            .Select(static item => new QueueData(
                item.Id,
                item.Value.ToArray(),
                item.NumberOfTries(),
                item.IsLastTry(),
                item.GetLastRetryTimeTicks(),
                item.GetHttpTimeoutTicks(),
                item.GetLastReservedIp()))
            .ToList();
    }

    private async Task WaitForLocalApplyAsync(CancellationToken cancellationToken = default)
    {
        var committedIndex = _cluster.AuditTrail.LastCommittedEntryIndex;
        if (committedIndex > 0L)
        {
            await _cluster.AuditTrail.WaitForApplyAsync(committedIndex, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static bool TryReadExpireAtFromKeyValues(
        SlimDataPayload data,
        string key,
        out long expireAtTicks)
    {
        expireAtTicks = 0;
        if (!data.KeyValues.TryGetValue(TtlKey(key), out var raw) ||
            raw.Length < sizeof(long))
        {
            return false;
        }
        expireAtTicks = BitConverter.ToInt64(raw.Span);
        return true;
    }

    private static bool TryReadExpireAtFromHashsets(
        SlimDataPayload data,
        string key,
        out long expireAtTicks)
    {
        expireAtTicks = 0;
        if (!data.Hashsets.TryGetValue(key, out var metadata) ||
            !metadata.TryGetValue(SlimDataInterpreter.HashsetTtlField, out var raw) ||
            raw.Length < sizeof(long))
        {
            return false;
        }
        expireAtTicks = BitConverter.ToInt64(raw.Span);
        return true;
    }

    private async Task MasterWaitForLeaseToken(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_cluster.ConsensusToken.IsCancellationRequested)
                {
                    await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                if (_cluster.LeadershipToken.IsCancellationRequested)
                    return;
                if (_cluster.TryGetLeaseToken(out var leaseToken) &&
                    !leaseToken.IsCancellationRequested)
                {
                    return;
                }

                await Task.Delay(25, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SlimDataUnavailableException("Raft quorum or leader lease is unavailable.", ex);
        }
    }

    private async Task<EndPoint> GetAndWaitForLeader(
        CancellationToken cancellationToken = default)
    {
        if (!_protocolCompatibility.IsCompatible)
        {
            throw new SlimDataUnavailableException(
                $"SlimData Raft command protocol is incompatible or unavailable: {_protocolCompatibility.Reason}");
        }

        var startedAt = Stopwatch.GetTimestamp();
        while (_cluster.Leader is null &&
               Stopwatch.GetElapsedTime(startedAt) < _timeMaxToWaitForLeader)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return _cluster.Leader?.EndPoint
               ?? throw new SlimDataUnavailableException("No Raft leader is currently available.");
    }

    private static void ThrowIfSlimDataUnavailable(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.GatewayTimeout)
        {
            throw new SlimDataUnavailableException(
                $"Raft quorum or leader lease is unavailable (HTTP {(int)response.StatusCode}).");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync().ConfigureAwait(false);
    }
}
#pragma warning restore CA2252
